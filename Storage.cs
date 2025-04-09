using System;
using System.Data.Common;
using System.Text.RegularExpressions;
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
            if (Debug)
            {
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
                Console.WriteLine("Storage: {0}", commandText);
                for (var i = 0; i < parameters.Length; i++)
                {
                    Console.WriteLine("Storage:   {0,2}  {1}", i, parameters[i]);
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

        public async Task<string> GetFieldType(string table, string field)
        {
            return (string)await ExecuteScalarAsync("SELECT type FROM pragma_table_info(@Param0) WHERE name = @Param1", table, field);
        }

        public async Task<bool> AlterTableAlterColumnType(string table, string column, string type, Func<Task> preCheck = null, Func<Task> postUpdate = null)
        {
            var patternColumnsInSchema = new Regex("^CREATE TABLE \"?\\w+\"? \\((.*)\\)$");
            var patternColumnType = new Regex($@"(?<={column} +)\w+(?= |,)");

            var schema = (string)await ExecuteScalarAsync($"SELECT sql FROM sqlite_master WHERE type = 'table' AND name = @Param0", table);

            if (!patternColumnsInSchema.IsMatch(schema)) throw new InvalidOperationException($"Failed to match columns in schema: {schema}");
            var oldColumns = patternColumnsInSchema.Replace(schema, "$1");

            if (!patternColumnType.IsMatch(oldColumns)) throw new InvalidOperationException($"Failed to match column type for {column} in old columns: {oldColumns}");
            var newColumns = patternColumnType.Replace(oldColumns, type);

            if (Debug)
            {
                Console.WriteLine($"AlterTableAlterColumnType({table}, {column}, {type})");
                Console.WriteLine($"  Schema:      {schema}");
                Console.WriteLine($"  Old columns: {oldColumns}");
                Console.WriteLine($"  New columns: {newColumns}");
            }
            if (oldColumns == newColumns) return false;

            if (preCheck != null) await preCheck();

            // Plan from https://www.sqlite.org/lang_altertable.html#making_other_kinds_of_table_schema_changes
            // 1. If foreign key constraints are enabled, disable them using PRAGMA foreign_keys=OFF.
            await ExecuteNonQueryAsync("PRAGMA foreign_keys=OFF");
            // 2. Start a transaction.
            await ExecuteNonQueryAsync("BEGIN TRANSACTION");
            // 3. Remember the format of all indexes, triggers, and views associated with table X. This information will be needed in step 8 below. One way to do this is to run a query like the following: SELECT type, sql FROM sqlite_schema WHERE tbl_name='X'.
            // 4. Use CREATE TABLE to construct a new table "new_X" that is in the desired revised format of table X. Make sure that the name "new_X" does not collide with any existing table name, of course.
            await ExecuteNonQueryAsync($"CREATE TABLE Migration_{table} ({newColumns})");
            // 5. Transfer content from X into new_X using a statement like: INSERT INTO new_X SELECT ... FROM X.
            await ExecuteNonQueryAsync($"INSERT INTO Migration_{table} SELECT * FROM {table}");
            // 6. Drop the old table X: DROP TABLE X.
            await ExecuteNonQueryAsync($"DROP TABLE {table}");
            // 7. Change the name of new_X to X using: ALTER TABLE new_X RENAME TO X.
            await ExecuteNonQueryAsync($"ALTER TABLE Migration_{table} RENAME TO {table}");
            // 8. Use CREATE INDEX, CREATE TRIGGER, and CREATE VIEW to reconstruct indexes, triggers, and views associated with table X. Perhaps use the old format of the triggers, indexes, and views saved from step 3 above as a guide, making changes as appropriate for the alteration.
            // 9. If any views refer to table X in a way that is affected by the schema change, then drop those views using DROP VIEW and recreate them with whatever changes are necessary to accommodate the schema change using CREATE VIEW.
            // 10. If foreign key constraints were originally enabled then run PRAGMA foreign_key_check to verify that the schema change did not break any foreign key constraints.

            if (postUpdate != null) await postUpdate();

            // 11. Commit the transaction started in step 2.
            await ExecuteNonQueryAsync("COMMIT TRANSACTION");
            // 12. If foreign keys constraints were originally enabled, reenable them now.
            await ExecuteNonQueryAsync("PRAGMA foreign_keys=ON");

            return true;
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
