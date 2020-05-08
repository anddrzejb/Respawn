# Respawn

Respawn is a small utility to help in resetting test databases to a clean state. Instead of deleting data at the end of a test or rolling back a transaction, Respawn [resets the database back to a clean checkpoint](http://lostechies.com/jimmybogard/2013/06/18/strategies-for-isolating-the-database-in-tests/) by intelligently deleting data from tables.

Detailed information on how to use it is on master branch of this fork. 

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
