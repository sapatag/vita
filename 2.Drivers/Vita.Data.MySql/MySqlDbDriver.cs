﻿using System;
using System.Data;
using System.Diagnostics;
using System.Linq;

using Vita.Common;
using Vita.Entities;
using Vita.Entities.Model;
using Vita.Data;
using Vita.Data.Model;
using Vita.Data.Driver;
using System.Collections.Generic;
using MySql.Data.MySqlClient;
using Vita.Entities.Logging;

namespace Vita.Data.MySql {
  /* Note:
   * 1. Add the following options to sql-mode variable in my.ini file: 
   *      ANSI_QUOTES
   * 2. MySql supports ordered columns in indexes, but there's no way to get this information when loading index columns from the database. So we suppress ordering; 
   *     we also set all column direction to ASC after construction DbModel
   * 3. Connection string must have ' Old Guids=true' to properly handle Guids
   
   */

  public class MySqlDbDriver : DbDriver {
    public const DbFeatures MySqlFeatures = DbFeatures.Schemas | DbFeatures.StoredProcedures | DbFeatures.OutputParameters
        | DbFeatures.Views | DbFeatures.DefaultCaseInsensitive
        | DbFeatures.ReferentialConstraints | DbFeatures.Paging | DbFeatures.BatchedUpdates | DbFeatures.TreatBitAsInt;
    public const DbOptions DefaultMySqlDbOptions = DbOptions.Default | DbOptions.AutoIndexForeignKeys | DbOptions.IgnoreTableNamesCase;

    public MySqlDbDriver() : base(DbServerType.MySql, MySqlFeatures)  {
      base.StoredProcParameterPrefix = "prm";
      base.DynamicSqlParameterPrefix = "@";
      base.CommandCallFormat = "CALL {0} ({1});";
      // base.CommandCallOutParamFormat = "{0}"; -- MySql does not require 'Out' spec in parameter reference
      base.BatchBeginTransaction = "START TRANSACTION;";
      base.BatchCommitTransaction = "COMMIT;";
      base.DefaultLikeEscapeChar = '/';
    }

    public override bool IsSystemSchema(string schema) {
      if(string.IsNullOrWhiteSpace(schema))
        return false;
      schema = schema.ToLowerInvariant();
      switch(schema) {
        case "information_schema":
        case "performance_schema":
        case "sys":
          return true;
        default:
          return false;
      }
    }


    // overrides
    protected override DbTypeRegistry CreateTypeRegistry() {
      return new MySqlTypeRegistry(this); 
    }
    public override IDbConnection CreateConnection(string connectionString) {
      return new MySqlConnection(connectionString);
    }

    public override DbSqlBuilder CreateDbSqlBuilder(DbModel dbModel) {
      return new MySqlDbSqlBuilder(dbModel); 
    }
    public override DbModelUpdater CreateDbModelUpdater(DbSettings settings) {
      return new MySqlDbModelUpdater(settings);
    }
    public override DbModelLoader CreateDbModelLoader(DbSettings settings, SystemLog log) {
      return new MySqlDbModelLoader(settings, log);
    }

    public override Vita.Data.Driver.LinqSqlProvider CreateLinqSqlProvider(DbModel dbModel) {
      return new MySqlLinqSqlProvider(dbModel);
    }

    public override string FormatFullName(string schema, string name) {
      return string.Format("\"{0}\".\"{1}\"", schema, name);
    }

    public override object ExecuteCommand(IDbCommand command, DbExecutionType executionType) {
      try {
        return base.ExecuteCommand(command, executionType);
      } catch(Exception) {
        //Strange behavior in MySql - sometimes, right exception thrown from db, the next (insert?) statement is not executed properly, 
        // and in effect ignored. To see this behavior, comment the following line and run TestUniqueKey test in UnitTests.Basic assembly. 
        // The second part of the test fails, because the first insert statement for driver Mindy is ignored, Mindy is not inserted, 
        // and then next insert of Molly driver succeeds and does not throw 'Unique index violation' exception. 
        // Did not find any clues why it happens. 
        ExecuteDummyPostErrorSelect(command.Connection); 
        throw; 
      }
    }

    //Dummy SELECT statement to execute right after error. See comment in ExecuteCommand
    private void ExecuteDummyPostErrorSelect(IDbConnection connection) {
      try {
        var newCmd = connection.CreateCommand();
        newCmd.CommandText = "SELECT '1';";
        if(connection.State != ConnectionState.Open)
          connection.Open();
        newCmd.ExecuteScalar();
      } catch {  }
    }

    public override void ClassifyDatabaseException(DataAccessException dataException, IDbCommand command = null) {
      var sqlEx = dataException.InnerException as MySqlException;
      if (sqlEx == null) return;
      dataException.ProviderErrorNumber = sqlEx.Number;
      switch (sqlEx.Number) {
        case 1062:
          var indexName = GetIndexName(sqlEx.Message);
          dataException.SubType = DataAccessException.SubTypeUniqueIndexViolation;
          dataException.Data[DataAccessException.KeyDbKeyName] = indexName;
          break;
        case 1451: //integrity violation
          dataException.SubType = DataAccessException.SubTypeIntegrityViolation;
          break; 
        default: 
          break; 
      } //switch
    }

    private string GetIndexName(string message) {
      try {
        // Sample message: Duplicate entry 'MS Books' for key 'IXU_Publisher_Name'
        var constrNameTag = "for key '";
        var tagPos = message.IndexOf(constrNameTag);
        var start = tagPos + constrNameTag.Length;
        var end = message.IndexOf("'", start);
        var indexName = message.Substring(start, end - start).Trim();
        return indexName;
      } catch (Exception ex) {
        return "(failed to identify index in message: " + ex.Message + ")"; //
      }
    }

    public override IDbDataParameter AddParameter(IDbCommand command, DbParamInfo prmInfo) {
      var prm = (MySqlParameter)base.AddParameter(command, prmInfo);
      prm.MySqlDbType = (MySqlDbType)prmInfo.TypeInfo.VendorDbType.VendorDbType;
      return prm;
    }

    public override void OnDbModelConstructed(DbModel dbModel) {
      foreach (var table in dbModel.Tables) {
        // Names of PK constraints in MySql is 'PRIMARY', cannot be changed
        // PK is always used as clustered index, but we consider clustered indexes as not supported in MySql
        if (table.PrimaryKey != null) 
          table.PrimaryKey.Name = "PRIMARY";

        // All foreign keys have a supporting index; if there's no matching index already, it is created automatically.
        foreach(var key in table.Keys) {
          if (key.KeyType.IsSet(KeyType.ForeignKey)) {
            var supportingIndex = DbModelHelper.FindMatchingIndex(key, table.Keys);
            if (supportingIndex == null) //if no supporting index, then mark this key as an index
              key.KeyType |= KeyType.Index;
          }
          //Drop descending flag - see notes at the top of the file
          // MySql supports ordered columns in indexes, but there's no way to get this information 
          // when loading index columns from the database - at least I don't know any 
          //  so we set all column direction to ASC after construction DbModel
          foreach(var kc in key.KeyColumns)
            kc.Desc = false; 
        }
        // auto_increment (identity) columns must be associated with key (PK or index). For auto-inc columns that are NOT PKs we create artificial index
        var autoIncCols = table.Columns.Where(c => c.Flags.IsSet(DbColumnFlags.Identity));
        foreach (var col in autoIncCols) {
          if (col.Flags.IsSet(DbColumnFlags.PrimaryKey))
            continue;
          var ind = new DbKeyInfo(col.ColumnName, col.Table, KeyType.Index); //added automatically to table.Keys list
          ind.KeyColumns.Add(new DbKeyColumnInfo(col));
        }
      
      }     
      // Remove double-quotes from StoredProcedure names - MySqlDriver does not like it
      foreach (var cmd in dbModel.Commands) {
        cmd.FullCommandName = cmd.FullCommandName.Replace("\"", "");
      }
      base.OnDbModelConstructed(dbModel);
    }

  }//class
}
