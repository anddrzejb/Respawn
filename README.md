# Respawn

Respawn is a small utility to help in resetting test databases to a clean state. Instead of deleting data at the end of a test or rolling back a transaction, Respawn [resets the database back to a clean checkpoint](http://lostechies.com/jimmybogard/2013/06/18/strategies-for-isolating-the-database-in-tests/) by intelligently deleting data from tables.

To use, create a `Checkpoint` and initialize with tables you want to skip, or schemas you want to keep/ignore:

```csharp
private static Checkpoint checkpoint = new Checkpoint
{
    TablesToIgnore = new[]
    {
        "sysdiagrams",
        "tblUser",
        "tblObjectType",
    },
    SchemasToExclude = new []
    {
        "RoundhousE"
    }
};
```
Or if you want to use a different database:
```csharp
private static Checkpoint checkpoint = new Checkpoint
{
    SchemasToInclude = new []
    {
        "public"
    },
    DbAdapter = DbAdapter.Postgres
};
```

In your tests, in the fixture setup, reset your checkpoint:
```csharp
await checkpoint.Reset("MyConnectionStringName");
```
or if you're using a database besides SQL Server, pass an open `DbConnection`:
```csharp
using (var conn = new NpgsqlConnection("ConnectionString"))
{
    await conn.OpenAsync();

    await checkpoint.Reset(conn);
}
```

## How does it work?
Respawn examines the SQL metadata intelligently to build a deterministic order of tables to delete based on foreign key relationships between tables. It navigates these relationships to build a DELETE script starting with the tables with no relationships and moving inwards until all tables are accounted for.

Once this in-order list of tables is created, the Checkpoint object keeps this list of tables privately so that the list of tables and the order is only calculated once.

In your tests, you Reset your checkpoint before each test run. If there are any tables/schemas that you don't want to be cleared out, include these in the configuration of your Checkpoint.

In benchmarks, a deterministic deletion of tables is faster than truncation, since truncation requires disabling or deleting foreign key constraints. Deletion results in easier test debugging/maintenance, as transaction rollbacks/post-test deletion still rely on that mechanism at the beginning of each test. If data comes in from another source, your test might fail. Respawning to your checkpoint assures you have a known starting point before each test.

### Installing Respawn

You should install [Respawn with NuGet](https://www.nuget.org/packages/Respawn):

    Install-Package Respawn

Or via the .NET Core CLI:

    dotnet add package Respawn

This command from Package Manager Console will download and install Respawn.

### Local development

To install and run local dependencies needed for tests, (PostgreSQL and MySQL) install Docker for Windows and from the command line at the solution root run:

```
docker-compose up -d
```

This will pull down the latest container images and run them. You can then run the local build/tests.

# Respawn.Jet

Has been created to tackle to problem of testing legacy applications using MS Access Jet technology.

The Jet engine has a lot of limitations that forced a different approach to clearing up the tables. 
The relationships have to be removed one by one and then reinstated after record deletion. Jet engine still observes integrity constraints that are defined within a table, event when a full table delete is issued.

Jet engine does not offer schema functionality, however the `Checkpoint` has been tweaked to support linked tables and odbc linked tables using `SchemaToInclude` and `SchemaToExclue`:

```csharp
private static Checkpoint checkpoint = new Checkpoint
{
    TablesToIgnore = new[]
    {
        "tblUser",
        "tblObjectType",
    },
    SchemasToInclude = new []
    {
        "dbo",                  //this is a constant that represents tables stored in current database
        "C:\Db\Test.mdb",       //will inlcude all tables that were linked to current database from C:\Db\Test.mdb
        "MyDbOnSqlServer"       //will inlcude all tables that have in their connection string segment "DATABASE=MyDbOnSqlServer"
    }
};
```

To use it with Jet engine:
```csharp
private static Checkpoint checkpoint = new Checkpoint
{
    SchemasToInclude = new []
    {
        "public"
    },
    DbAdapter = DbAdapter.Jet
};
```

## Prerequisits

Respawn.Jet after connecting to the database, will try to access system tables MSysObjects and MSysRelationships. By default those tables are off-limits for ODBC connections. The testing database needs to be set to allow connections to them. To do that, open your testing database in design mode, go to VBE and in the immediate window (CTRL+g) execute following lines:
```VBA
CurrentProject.Connection.Execute "GRANT SELECT ON MSysObjects TO Admin;"
CurrentProject.Connection.Execute "GRANT SELECT ON MSysRelationships TO Admin;"
```
![Grant access to system tables](https://github.com/anddrzejb/Respawn/blob/JetEngineSupport/Info/VBE_Grant.PNG?raw=true)

To connect to .mdb database use connection string format:
```
"Driver={Microsoft Access Driver (*.mdb)};Dbq=d:\\Your\\Folder\\database.mdb;Uid=Admin;Pwd=;"
```

### Limitations

Respawn.Jet will tear down your integrity constraints and then rebuild them. However if your integrity constraints will be rebuild only with option `ON DELETE NO ACTION`. This is limitation of the Jet engine, that does not allow stating type of action when creating constraints using SQL through ODBC. I believe this should be fine for testing purposes, however if this is a requirement, then your tests need to be aware about this limitiation.

If `WithReseed = true` then all the tables that are expected to be deleted and have `Autoincrement` column need to be closed (including MS Access datasheet view).

During development it may happen that ODBC tracing is on (not so uncommon). However, tracing will terribly slow down the ODBC. For example a simple teardown of 3 tables (7 relationships) with trace was registering at over 10 000 miliseconds, but after trace shutdown, the teardown came down to around 50 miliseconds. These values can differ on different setups, but it should reflect the impact of ODBC tracing.
