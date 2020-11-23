using Respawn.Graph;

namespace Respawn
{
	using System;
	using System.Collections.Generic;
	using System.Data.Common;
	using System.Data.SqlClient;
	using System.Linq;
	using System.Threading.Tasks;

	[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "No queries are based on user input.")]
	public class Checkpoint
	{
		private GraphBuilder _graphBuilder;
		private IList<TemporalTable> _temporalTables = new List<TemporalTable>();

		public string[] TablesToIgnore { get; set; } = new string[0];
		public string[] TablesToInclude { get; set; } = new string[0];
		public string[] SchemasToInclude { get; set; } = new string[0];
		public string[] SchemasToExclude { get; set; } = new string[0];
		public string DeleteSql { get; private set; }
		public string ReseedSql { get; private set; }
		public bool CheckTemporalTables { get; set; } = false;
		internal string DatabaseName { get; private set; }
		public bool WithReseed { get; set; } = false;
		public IDbAdapter DbAdapter { get; set; } = Respawn.DbAdapter.SqlServer;

		public int? CommandTimeout { get; set; }

		public virtual async Task Reset(string nameOrConnectionString)
		{
			using (var connection = new SqlConnection(nameOrConnectionString))
			{
				await connection.OpenAsync();

				await Reset(connection);
			}
		}

		public virtual async Task Reset(DbConnection connection)
		{
			if (string.IsNullOrWhiteSpace(DeleteSql))
			{
				DatabaseName = connection.Database;
				await BuildDeleteTables(connection);
			}

			if (_temporalTables.Any())
			{
				var turnOffVersioningCommandText = DbAdapter.BuildTurnOffSystemVersioningCommandText(_temporalTables);
				await ExecuteAlterSystemVersioningAsync(connection, turnOffVersioningCommandText);
			}

			await ExecuteDeleteSqlAsync(connection);

			if (_temporalTables.Any())
			{
				var turnOnVersioningCommandText = DbAdapter.BuildTurnOnSystemVersioningCommandText(_temporalTables);
				await ExecuteAlterSystemVersioningAsync(connection, turnOnVersioningCommandText);
			}
		}
		private async Task ExecuteAlterSystemVersioningAsync(DbConnection connection, string commandText)
		{
			using (var tx = connection.BeginTransaction())
			using (var cmd = connection.CreateCommand())
			{
				cmd.CommandTimeout = CommandTimeout ?? cmd.CommandTimeout;
				cmd.CommandText = commandText;
				cmd.Transaction = tx;

				await cmd.ExecuteNonQueryAsync();

				tx.Commit();
			}
		}

		private async Task ExecuteDeleteSqlAsync(DbConnection connection)
		{
			using (var tx = connection.BeginTransaction())
			using (var cmd = connection.CreateCommand())
			{
				cmd.CommandTimeout = CommandTimeout ?? cmd.CommandTimeout;
				cmd.Transaction = tx;
				if (DbAdapter == Respawn.DbAdapter.Jet)
				{
					foreach (var statement in DeleteSql.Remove(DeleteSql.Length - 6).Split(new string[] { ";##;" }, StringSplitOptions.None))
					{
						cmd.CommandText = statement;
						try
						{
							cmd.ExecuteNonQuery();
						}
						catch (Exception ex) when (ex.GetType().Name == "OdbcException"
										&& ex.Message.Contains("[Microsoft][ODBC Microsoft Access Driver] Invalid field data type.")
										&& statement.Contains("COUNTER(1,1)"))
						{
							//Sometimes there is a problem with older databases to reseed. It is good enough to 
							//change the seed to NUMBER and then back to COUNTER to solve this problem. If this happens,
							//it should only be done first time and then the problem should no longer happen.
							cmd.CommandText = statement.Replace("COUNTER(1,1)", "NUMBER");
							cmd.ExecuteNonQuery();
							cmd.CommandText = statement;
							cmd.ExecuteNonQuery();
						}
						catch (Exception ex) when (ex.GetType().Name == "OdbcException"
										&& ex.Message.Contains("[Microsoft][ODBC Microsoft Access Driver] Record is deleted."))
						{
							new System.Threading.ManualResetEvent(false).WaitOne(100);
							cmd.ExecuteNonQuery();
						}
					}
				}
				else
				{
					cmd.CommandText = DeleteSql;
					int deadlockRetryCounter = 0;
					while (deadlockRetryCounter < 3 && deadlockRetryCounter >= 0)
					{
						try
						{
							await cmd.ExecuteNonQueryAsync();
							deadlockRetryCounter = -1;
						}
						//SQL Server error specific (deadlock)
						catch (SqlException ex) when (ex.Number == 1205 && DbAdapter == Respawn.DbAdapter.SqlServer)
						{
							deadlockRetryCounter++;
						}
						catch (Exception)
						{
							deadlockRetryCounter = 5;
							throw;
						}
					}
					if (ReseedSql != null)
					{
						cmd.CommandText = ReseedSql;
						await cmd.ExecuteNonQueryAsync();
					}
				}
				tx.Commit();
			}
		}

		private async Task BuildDeleteTables(DbConnection connection)
		{
			var allTables = await GetAllTables(connection);

			if (CheckTemporalTables && DoesDbSupportsTemporalTables(connection))
			{
				_temporalTables = await GetAllTemporalTables(connection);
			}

			var allRelationships = await GetRelationships(connection);

			_graphBuilder = new GraphBuilder(allTables, allRelationships, DbAdapter == Respawn.DbAdapter.Jet);

			if (WithReseed)
			{
				if (DbAdapter == Respawn.DbAdapter.Jet)
					allTables = await GetTableSeeds(connection, allTables);
				else
					ReseedSql = DbAdapter.BuildReseedSql(_graphBuilder.ToDelete);
			}
			else
				ReseedSql = null;
			DeleteSql = DbAdapter.BuildDeleteCommandText(_graphBuilder);
		}

		private async Task<HashSet<Table>> GetTableSeeds(DbConnection connection, HashSet<Table> tables)
		{
			using (var cmd = connection.CreateCommand())
			{
				foreach (Table table in tables)
				{
					cmd.CommandText = DbAdapter.BuildFullColumnQueryCommandText(table);
					using (var reader = await cmd.ExecuteReaderAsync())
					{
						for (int i = 0; i < reader.FieldCount; i++)
						{
							if (reader.GetDataTypeName(i) == DbAdapter.SeedColumnTypeName())
							{
								table.SeedColumn = reader.GetName(i);
								break;
							}
						}
					}
				}
			}
			return tables;
		}

		private async Task<HashSet<Relationship>> GetRelationships(DbConnection connection)
		{
			var rels = new HashSet<Relationship>();
			var commandText = DbAdapter.BuildRelationshipCommandText(this);

			using (var cmd = connection.CreateCommand())
			{
				cmd.CommandText = commandText;
				using (DbDataReader reader = await cmd.ExecuteReaderAsync())
				{
					while (await reader.ReadAsync())
					{
						rels.Add(new Relationship(
							reader.IsDBNull(0) ? null : reader.GetString(0),
							reader.GetString(1),
							reader.IsDBNull(2) ? null : reader.GetString(2), 
							reader.GetString(3),
							reader.GetString(4),
							reader.FieldCount >= 6 && !reader.IsDBNull(5) ? reader.GetString(5) : null ,
							reader.FieldCount == 7 && !reader.IsDBNull(6) ? reader.GetString(6) : null 
							));
					}
				}
			}

			return rels;
		}

		private async Task<HashSet<Table>> GetAllTables(DbConnection connection)
		{
			var tables = new HashSet<Table>();

			string commandText = DbAdapter.BuildTableCommandText(this);

			using (var cmd = connection.CreateCommand())
			{
				cmd.CommandText = commandText;
				using (var reader = await cmd.ExecuteReaderAsync())
				{
					while (await reader.ReadAsync())
					{
						tables.Add(new Table(reader.IsDBNull(0) ? null : reader.GetString(0), reader.GetString(1)));
					}
				}
			}

			return tables;
		}

		private async Task<IList<TemporalTable>> GetAllTemporalTables(DbConnection connection)
		{
			var tables = new List<TemporalTable>();

			string commandText = DbAdapter.BuildTemporalTableCommandText(this);

			using (var cmd = connection.CreateCommand())
			{
				cmd.CommandText = commandText;
				using (var reader = await cmd.ExecuteReaderAsync())
				{
					while (await reader.ReadAsync())
					{
						tables.Add(new TemporalTable(reader.IsDBNull(0) ? null : reader.GetString(0), reader.GetString(1), reader.GetString(2)));
					}
				}
			}

			return tables;
		}

		private bool DoesDbSupportsTemporalTables(DbConnection connection)
		{
			if (DbAdapter.GetType() == Respawn.DbAdapter.SqlServer.GetType())
			{
				const int SqlServer2016MajorBuildVersion = 13;
				string serverVersion = connection.ServerVersion;
				string[] serverVersionDetails = serverVersion.Split(new string[] { "." }, StringSplitOptions.None);
				int versionNumber = int.Parse(serverVersionDetails[0]);
				return versionNumber >= SqlServer2016MajorBuildVersion;
			}
			return false;
		}
	}
}
