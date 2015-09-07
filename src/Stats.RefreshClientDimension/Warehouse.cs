// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Threading.Tasks;
using Stats.ImportAzureCdnStatistics;

namespace Stats.RefreshClientDimension
{
    internal class Warehouse
    {
        private const int _defaultCommandTimeout = 1800; // 30 minutes max

        public static async Task<IEnumerable<string>> GetUnknownUserAgents(SqlConnection connection)
        {
            var results = new List<string>();
            var command = connection.CreateCommand();
            command.CommandText = "[dbo].[GetUnknownUserAgents]";
            command.CommandType = CommandType.StoredProcedure;
            command.CommandTimeout = _defaultCommandTimeout;

            using (var dataReader = await command.ExecuteReaderAsync())
            {
                while (await dataReader.ReadAsync())
                {
                    var userAgent = dataReader.GetString(0);
                    results.Add(userAgent);
                }
            }

            return results;
        }

        public static async Task<IDictionary<string, int>> EnsureRecognizedUserAgentsExist(SqlConnection connection, IDictionary<string, ClientDimension> recognizedUserAgents)
        {
            var results = new Dictionary<string, int>();

            var command = connection.CreateCommand();
            command.CommandText = "[dbo].[EnsureClientDimensionsExist]";
            command.CommandType = CommandType.StoredProcedure;
            command.CommandTimeout = _defaultCommandTimeout;

            var parameterValue = ClientDimensionTableType.CreateDataTable(recognizedUserAgents);

            var parameter = command.Parameters.AddWithValue("clients", parameterValue);
            parameter.SqlDbType = SqlDbType.Structured;
            parameter.TypeName = "[dbo].[ClientDimensionTableType]";

            using (var dataReader = await command.ExecuteReaderAsync())
            {
                while (await dataReader.ReadAsync())
                {
                    var clientDimensionId = dataReader.GetInt32(0);
                    var userAgent = dataReader.GetString(1);

                    results.Add(userAgent, clientDimensionId);
                }
            }

            return results;
        }

        public static async Task PatchClientDimension(SqlConnection connection, IDictionary<string, ClientDimension> recognizedUserAgents, IDictionary<string, int> recognizedUserAgentsWithClientDimensionId)
        {
            var count = recognizedUserAgentsWithClientDimensionId.Count;
            var i = 0;

            foreach (var kvp in recognizedUserAgentsWithClientDimensionId)
            {
                i++;

                var userAgent = kvp.Key;
                var clientDimensionId = kvp.Value;

                var command = connection.CreateCommand();
                command.CommandText = "[dbo].[PatchClientDimensionForUserAgent]";
                command.CommandType = CommandType.StoredProcedure;
                command.CommandTimeout = _defaultCommandTimeout;

                command.Parameters.AddWithValue("NewClientDimensionId", clientDimensionId);
                command.Parameters.AddWithValue("UserAgent", userAgent);

                Trace.WriteLine(string.Format("[{0}/{1}]: Client Id '{2}', User Agent '{3}'", i, count, clientDimensionId, userAgent));

                await command.ExecuteNonQueryAsync();
            }
        }
    }
}