using System.Data.Common;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace ForumScanner
{
    public class Storage
    {
        DbConnection Connection;

        public Storage(string connectionString)
        {
            Connection = new SqliteConnection();
            Connection.ConnectionString = connectionString;
        }

        public async Task Open()
        {
            await Connection.OpenAsync();
        }

        public void Close()
        {
            Connection.Close();
        }

        public async Task ExecuteNonQueryAsync(string commandText)
        {
            var command = Connection.CreateCommand();
            command.CommandText = commandText;
            await command.ExecuteNonQueryAsync();
        }

        public async Task<object> ExecuteScalarAsync(string commandText, params object[] parameters)
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
            return await command.ExecuteScalarAsync();
        }
    }
}
