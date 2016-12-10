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
    }
}
