using System;
using System.Data.Common;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace ForumScanner
{
    public class Storage
    {
        readonly DbConnection Connection;
        readonly bool Debug;

        public Storage(string connectionString, bool debug)
        {
            Connection = new SqliteConnection();
            Connection.ConnectionString = connectionString;
            Debug = debug;
            if (Debug) {
                Console.WriteLine("Running in debug mode; not data changes will be persisted.");
            }
        }

        public async Task Open()
        {
            await Connection.OpenAsync();
        }

        public void Close()
        {
            Connection.Close();
        }

        public async Task ExecuteNonQueryAsync(string commandText, params object[] parameters)
        {
            if (Debug)
            {
                Console.WriteLine(commandText);
                for (var i = 0; i < parameters.Length; i++)
                {
                    Console.WriteLine("    {0,2}  {1}", i, parameters[i]);
                }
            }
            else
            {
                await CreateCommand(commandText, parameters).ExecuteNonQueryAsync();
            }
        }

        public async Task<object> ExecuteScalarAsync(string commandText, params object[] parameters)
        {
            return await CreateCommand(commandText, parameters).ExecuteScalarAsync();
        }

        DbCommand CreateCommand(string commandText, object[] parameters)
        {
            var command = Connection.CreateCommand();

            command.CommandText = commandText;

            for (var i = 0; i < parameters.Length; i++)
            {
                var param = command.CreateParameter();
                param.ParameterName = $"@Param{i}";
                param.Value = parameters[i];
                command.Parameters.Add(param);
            }

            return command;
        }
    }
}
