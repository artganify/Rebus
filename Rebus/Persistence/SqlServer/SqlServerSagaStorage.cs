﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Rebus.Exceptions;
using Rebus.Logging;
using Rebus.Reflection;
using Rebus.Sagas;

namespace Rebus.Persistence.SqlServer
{
    /// <summary>
    /// Implementation of <see cref="ISagaStorage"/> that persists saga data as a Newtonsoft JSON.NET-serialized object to a table in SQL Server.
    /// Correlation properties are stored in a separate index table, allowing for looking up saga data instanes based on the configured correlation
    /// properties
    /// </summary>
    public class SqlServerSagaStorage : ISagaStorage
    {
        const int MaximumSagaDataTypeNameLength = 40;
        const string IdPropertyName = nameof(ISagaData.Id);
        const bool IndexNullProperties = false;

        static readonly JsonSerializerSettings Settings = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.All };

        readonly ILog _log;
        readonly IDbConnectionProvider _connectionProvider;
        readonly string _dataTableName;
        readonly string _indexTableName;

        /// <summary>
        /// Constructs the saga storage, using the specified connection provider and tables for persistence.
        /// </summary>
		public SqlServerSagaStorage(IDbConnectionProvider connectionProvider, string dataTableName, string indexTableName, IRebusLoggerFactory rebusLoggerFactory)
        {
            if (connectionProvider == null) throw new ArgumentNullException(nameof(connectionProvider));
            if (dataTableName == null) throw new ArgumentNullException(nameof(dataTableName));
            if (indexTableName == null) throw new ArgumentNullException(nameof(indexTableName));
            if (rebusLoggerFactory == null) throw new ArgumentNullException(nameof(rebusLoggerFactory));
            _log = rebusLoggerFactory.GetCurrentClassLogger();
            _connectionProvider = connectionProvider;
            _dataTableName = dataTableName;
            _indexTableName = indexTableName;
        }

        /// <summary>
        /// Checks to see if the configured tables exist, creating them if necessary
        /// </summary>
        public void EnsureTablesAreCreated()
        {
            EnsureTablesAreCreatedAsync().Wait();
        }

        async Task EnsureTablesAreCreatedAsync()
        {
            using (var connection = await _connectionProvider.GetConnection())
            {
                var tableNames = connection.GetTableNames().ToList();

                var hasDataTable = tableNames.Contains(_dataTableName, StringComparer.OrdinalIgnoreCase);
                var hasIndexTable = tableNames.Contains(_indexTableName, StringComparer.OrdinalIgnoreCase);
                
                if (hasDataTable && hasIndexTable)
                {
                    return;
                }

                if (hasDataTable)
                {
                    throw new ApplicationException(
                        $"The saga index table '{_indexTableName}' does not exist, so the automatic saga schema generation tried to run - but there was already a table named '{_dataTableName}', which was supposed to be created as the data table");
                }

                if (hasIndexTable)
                {
                    throw new ApplicationException(
                        $"The saga data table '{_dataTableName}' does not exist, so the automatic saga schema generation tried to run - but there was already a table named '{_indexTableName}', which was supposed to be created as the index table");
                }

                _log.Info("Saga tables '{0}' (data) and '{1}' (index) do not exist - they will be created now", _dataTableName, _indexTableName);

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = string.Format(@"
CREATE TABLE [dbo].[{0}] (
	[id] [uniqueidentifier] NOT NULL,
	[revision] [int] NOT NULL,
	[data] [nvarchar](max) NOT NULL,
    CONSTRAINT [PK_{0}] PRIMARY KEY CLUSTERED 
    (
	    [id] ASC
    )
)
", _dataTableName);

                    await command.ExecuteNonQueryAsync();
                }

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = string.Format(@"
CREATE TABLE [dbo].[{0}] (
	[saga_type] [nvarchar](40) NOT NULL,
	[key] [nvarchar](200) NOT NULL,
	[value] [nvarchar](200) NOT NULL,
	[saga_id] [uniqueidentifier] NOT NULL,
    CONSTRAINT [PK_{0}] PRIMARY KEY CLUSTERED 
    (
	    [key] ASC,
	    [value] ASC,
	    [saga_type] ASC
    )
)

CREATE NONCLUSTERED INDEX [IX_{0}_saga_id] ON [dbo].[{0}]
(
	[saga_id] ASC
)
", _indexTableName);

                    await command.ExecuteNonQueryAsync();
                }

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = string.Format(@"
ALTER TABLE [dbo].[{0}] WITH CHECK 
    ADD CONSTRAINT [FK_{1}_id] FOREIGN KEY([saga_id])

REFERENCES [dbo].[{1}] ([id]) ON DELETE CASCADE
", _indexTableName, _dataTableName);

                    await command.ExecuteNonQueryAsync();
                }

                using (var command = connection.CreateCommand())
                {
                    command.CommandText =
                        $@"
ALTER TABLE [dbo].[{_indexTableName}] CHECK CONSTRAINT [FK_{_dataTableName}_id]
";

                    await command.ExecuteNonQueryAsync();
                }

                await connection.Complete();
            }
        }

        /// <summary>
        /// Queries the saga index for an instance with the given <paramref name="sagaDataType"/> with a
        /// a property named <paramref name="propertyName"/> and the value <paramref name="propertyValue"/>
        /// </summary>
        public async Task<ISagaData> Find(Type sagaDataType, string propertyName, object propertyValue)
        {
            if (sagaDataType == null) throw new ArgumentNullException(nameof(sagaDataType));
            if (propertyName == null) throw new ArgumentNullException(nameof(propertyName));
            if (propertyValue == null) throw new ArgumentNullException(nameof(propertyValue));

            using (var connection = await _connectionProvider.GetConnection())
            {
                using (var command = connection.CreateCommand())
                {
                    if (propertyName.Equals(IdPropertyName, StringComparison.InvariantCultureIgnoreCase))
                    {
                        command.CommandText = $@"SELECT TOP 1 [data] FROM [{_dataTableName}] WHERE [id] = @value";
                    }
                    else
                    {
                        command.CommandText =
                            $@"
SELECT TOP 1 [saga].[data] as 'data' FROM [{_dataTableName}] [saga] 
    JOIN [{
                                _indexTableName
                                }] [index] ON [saga].[id] = [index].[saga_id] 
WHERE [index].[saga_type] = @saga_type
    AND [index].[key] = @key 
    AND [index].[value] = @value";

                        var sagaTypeName = GetSagaTypeName(sagaDataType);

                        command.Parameters.Add("key", SqlDbType.NVarChar, propertyName.Length).Value = propertyName;
                        command.Parameters.Add("saga_type", SqlDbType.NVarChar, sagaTypeName.Length).Value = sagaTypeName;
                    }

                    var correlationPropertyValue = GetCorrelationPropertyValue(propertyValue);

                    command.Parameters.Add("value", SqlDbType.NVarChar, correlationPropertyValue.Length).Value = correlationPropertyValue;

                    var dbValue = await command.ExecuteScalarAsync();
                    var value = (string)dbValue;
                    if (value == null) return null;

                    try
                    {
                        return (ISagaData)JsonConvert.DeserializeObject(value, Settings);
                    }
                    catch (Exception exception)
                    {
                        throw new ApplicationException(
                            $"An error occurred while attempting to deserialize '{value}' into a {sagaDataType}", exception);
                    }
                }
            }
        }

        /// <summary>
        /// Serializes the given <see cref="ISagaData"/> and generates entries in the index for the specified <paramref name="correlationProperties"/>
        /// </summary>
        public async Task Insert(ISagaData sagaData, IEnumerable<ISagaCorrelationProperty> correlationProperties)
        {
            if (sagaData.Id == Guid.Empty)
            {
                throw new InvalidOperationException($"Saga data {sagaData.GetType()} has an uninitialized Id property!");
            }

            if (sagaData.Revision != 0)
            {
                throw new InvalidOperationException($"Attempted to insert saga data with ID {sagaData.Id} and revision {sagaData.Revision}, but revision must be 0 on first insert!");
            }

            using (var connection = await _connectionProvider.GetConnection())
            {
                using (var command = connection.CreateCommand())
                {
                    var data = JsonConvert.SerializeObject(sagaData, Formatting.Indented, Settings);

                    command.Parameters.Add("id", SqlDbType.UniqueIdentifier).Value = sagaData.Id;
                    command.Parameters.Add("revision", SqlDbType.Int).Value = sagaData.Revision;
                    command.Parameters.Add("data", SqlDbType.NVarChar).Value = data;

                    command.CommandText = $@"INSERT INTO [{_dataTableName}] ([id], [revision], [data]) VALUES (@id, @revision, @data)";
                    try
                    {
                        await command.ExecuteNonQueryAsync();
                    }
                    catch (SqlException sqlException)
                    {
                        if (sqlException.Number == SqlServerMagic.PrimaryKeyViolationNumber)
                        {
                            throw new ConcurrencyException("An exception occurred while attempting to insert saga data with ID {0}", sagaData.Id);
                        }

                        throw;
                    }
                }

                var propertiesToIndex = GetPropertiesToIndex(sagaData, correlationProperties);

                if (propertiesToIndex.Any())
                {
                    await CreateIndex(connection, sagaData, propertiesToIndex);
                }

                await connection.Complete();
            }
        }

        /// <summary>
        /// Updates the given <see cref="ISagaData"/> and generates entries in the index for the specified <paramref name="correlationProperties"/>
        /// </summary>
        public async Task Update(ISagaData sagaData, IEnumerable<ISagaCorrelationProperty> correlationProperties)
        {
            using (var connection = await _connectionProvider.GetConnection())
            {
                var revisionToUpdate = sagaData.Revision;
                sagaData.Revision++;

                try
                {
                    // first, delete existing index
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = $@"DELETE FROM [{_indexTableName}] WHERE [saga_id] = @id";
                        command.Parameters.Add("id", SqlDbType.UniqueIdentifier).Value = sagaData.Id;

                        await command.ExecuteNonQueryAsync();
                    }

                    // next, update or insert the saga
                    using (var command = connection.CreateCommand())
                    {
                        var data = JsonConvert.SerializeObject(sagaData, Formatting.Indented, Settings);

                        command.Parameters.Add("id", SqlDbType.UniqueIdentifier).Value = sagaData.Id;
                        command.Parameters.Add("current_revision", SqlDbType.Int).Value = revisionToUpdate;
                        command.Parameters.Add("next_revision", SqlDbType.Int).Value = sagaData.Revision;
                        command.Parameters.Add("data", SqlDbType.NVarChar).Value = data;

                        command.CommandText =
                            $@"
UPDATE [{_dataTableName}] 
    SET [data] = @data, [revision] = @next_revision 
    WHERE [id] = @id AND [revision] = @current_revision";

                        var rows = await command.ExecuteNonQueryAsync();

                        if (rows == 0)
                        {
                            throw new ConcurrencyException("Update of saga with ID {0} did not succeed because someone else beat us to it", sagaData.Id);
                        }
                    }

                    var propertiesToIndex = GetPropertiesToIndex(sagaData, correlationProperties);

                    if (propertiesToIndex.Any())
                    {
                        await CreateIndex(connection, sagaData, propertiesToIndex);
                    }

                    await connection.Complete();
                }
                catch
                {
                    sagaData.Revision--;
                    throw;
                }
            }
        }

        /// <summary>
        /// Deletes the given <see cref="ISagaData"/> and removes all its entries in the index
        /// </summary>
        public async Task Delete(ISagaData sagaData)
        {
            using (var connection = await _connectionProvider.GetConnection())
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = $@"DELETE FROM [{_dataTableName}] WHERE [id] = @id AND [revision] = @current_revision;";
                    command.Parameters.Add("id", SqlDbType.UniqueIdentifier).Value = sagaData.Id;
                    command.Parameters.Add("current_revision", SqlDbType.Int).Value = sagaData.Revision;

                    var rows = await command.ExecuteNonQueryAsync();

                    if (rows == 0)
                    {
                        throw new ConcurrencyException("Delete of saga with ID {0} did not succeed because someone else beat us to it", sagaData.Id);
                    }
                }

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = $@"DELETE FROM [{_indexTableName}] WHERE [saga_id] = @id";
                    command.Parameters.Add("id", SqlDbType.UniqueIdentifier).Value = sagaData.Id;
                    await command.ExecuteNonQueryAsync();
                }

                await connection.Complete();
            }
        }

        static string GetCorrelationPropertyValue(object propertyValue)
        {
            return (propertyValue ?? "").ToString();
        }

        async Task CreateIndex(IDbConnection connection, ISagaData sagaData, IEnumerable<KeyValuePair<string, string>> propertiesToIndex)
        {
            var sagaTypeName = GetSagaTypeName(sagaData.GetType());
            var propertiesToIndexList = propertiesToIndex.ToList();

            var parameters = propertiesToIndexList
                .Select((p, i) => new
                {
                    PropertyName = p.Key,
                    PropertyValue = GetCorrelationPropertyValue(p.Value),
                    PropertyNameParameter = $"n{i}",
                    PropertyValueParameter = $"v{i}"
                })
                .ToList();

            // lastly, generate new index
            using (var command = connection.CreateCommand())
            {
                // generate batch insert with SQL for each entry in the index
                var inserts = parameters
                    .Select(a =>
                        $@"
INSERT INTO [{_indexTableName}]
    ([saga_type], [key], [value], [saga_id]) 
VALUES
    (@saga_type, @{
                            a.PropertyNameParameter}, @{a.PropertyValueParameter}, @saga_id)
")
                    .ToList();

                var sql = string.Join(";" + Environment.NewLine, inserts);

                command.CommandText = sql;

                foreach (var parameter in parameters)
                {
                    command.Parameters.Add(parameter.PropertyNameParameter, SqlDbType.NVarChar).Value = parameter.PropertyName;
                    command.Parameters.Add(parameter.PropertyValueParameter, SqlDbType.NVarChar).Value = parameter.PropertyValue;
                }

                command.Parameters.Add("saga_type", SqlDbType.NVarChar).Value = sagaTypeName;
                command.Parameters.Add("saga_id", SqlDbType.UniqueIdentifier).Value = sagaData.Id;

                try
                {
                    await command.ExecuteNonQueryAsync();
                }
                catch (SqlException sqlException)
                {
                    if (sqlException.Number == SqlServerMagic.PrimaryKeyViolationNumber)
                    {
                        throw new ConcurrencyException("Could not update index for saga with ID {0} because of a PK violation - there must already exist a saga instance that uses one of the following correlation properties: {1}", sagaData.Id,
                            string.Join(", ", propertiesToIndexList.Select(p => $"{p.Key}='{p.Value}'")));
                    }

                    throw;
                }
            }

        }

        static string GetSagaTypeName(Type sagaDataType)
        {
            var sagaTypeName = sagaDataType.Name;

            if (sagaTypeName.Length > MaximumSagaDataTypeNameLength)
            {
                throw new InvalidOperationException(
                    $@"Sorry, but the maximum length of the name of a saga data class is currently limited to {MaximumSagaDataTypeNameLength} characters!
This is due to a limitation in SQL Server, where compound indexes have a 900 byte upper size limit - and
since the saga index needs to be able to efficiently query by saga type, key, and value at the same time,
there's room for only 200 characters as the key, 200 characters as the value, and 40 characters as the
saga type name.");
            }

            return sagaTypeName;
        }

        static List<KeyValuePair<string, string>> GetPropertiesToIndex(ISagaData sagaData, IEnumerable<ISagaCorrelationProperty> correlationProperties)
        {
            return correlationProperties
                .Select(p => p.PropertyName)
                .Select(path =>
                {
                    var value = Reflect.Value(sagaData, path);

                    return new KeyValuePair<string, string>(path, value != null ? value.ToString() : null);
                })
                .Where(kvp => IndexNullProperties || kvp.Value != null)
                .ToList();
        }
    }
}