﻿/*
 License: http://www.apache.org/licenses/LICENSE-2.0 
 Home page: http://code.google.com/p/dapper-dot-net/
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.Data;
using Dapper;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Data.Common;
using System.Diagnostics;
using System.Reflection.Emit;

namespace Dapper
{
    /// <summary>
    /// A container for a database, assumes all the tables have an Id column named Id
    /// </summary>
    /// <typeparam name="TDatabase"></typeparam>
    public abstract class Database<TDatabase> : IDisposable where TDatabase : Database<TDatabase>, new()
    {
        public class Table<T>
        {
            internal Database<TDatabase> database;
            internal string tableName;
            internal string likelyTableName;

            public Table(Database<TDatabase> database, string likelyTableName)
            {
                this.database = database;
                this.likelyTableName = likelyTableName;
            }

            public string TableName
            {
                get
                {
                    tableName = tableName ?? database.DetermineTableName<T>(likelyTableName);
                    return tableName;
                }
            }

            /// <summary>
            /// Insert a row into the db
            /// </summary>
            /// <param name="data">Either DynamicParameters or an anonymous type or concrete type</param>
            /// <returns></returns>
            public virtual int? Insert(dynamic data)
            {
                return Insert<int?>(data);
            }
            /// <summary>
            /// Insert a row into the db
            /// </summary>
            /// <typeparam name="TId">Type of the ID column</typeparam>
            /// <param name="data">Either DynamicParameters or an anonymous type or concrete type</param>
            /// <returns></returns>
            /// <remarks>Currently, TId should be a nullable type like int?, ulong?, ...</remarks>
            public virtual TId Insert<TId>(dynamic data)
            {
                var o = (object)data;
                List<string> paramNames = GetParamNames(o);

                string cols = string.Join(",", paramNames);
                string cols_params = string.Join(",", paramNames.Select(p => "@" + p));
                var sql = String.Format(database.SqlCrudTemplate.InsertTemplate + ";" /*+ database.SqlCrudTemplate.SelectLastInsertedId*/, TableName, cols, cols_params);

                var ret = database.Execute(sql, o);
                var kdkk = database.Query<TId>(database.SqlCrudTemplate.SelectLastInsertedId).Single();
                sql = database.SqlCrudTemplate.SelectLastInsertedId;
                return database.Query<TId>(sql, o).Single();
            }

            /// <summary>
            /// Update a record in the DB
            /// </summary>
            /// <param name="id"></param>
            /// <param name="data"></param>
            /// <returns></returns>
            public int Update(int id, dynamic data)
            {
                return Update<int>(id, data);
            }

            /// <summary>
            /// Update a record in the DB
            /// </summary>
            /// <typeparam name="TId">Type of the ID column</typeparam>
            /// <param name="id"></param>
            /// <param name="data"></param>
            /// <returns></returns>
            public int Update<TId>(TId id, dynamic data)
            {
                List<string> paramNames = GetParamNames((object)data);

                var builder = new StringBuilder();
                builder.Append("update ").Append(TableName).Append(" set ");
                builder.AppendLine(string.Join(",", paramNames.Where(n => n != "Id").Select(p => p + "= @" + p)));
                builder.Append("where Id = @Id");

                DynamicParameters parameters = new DynamicParameters(data);
                parameters.Add("Id", id);

                return database.Execute(builder.ToString(), parameters);
            }

            /// <summary>
            /// Delete a record from the DB
            /// </summary>
            /// <param name="id"></param>
            /// <returns></returns>
            public bool Delete(int id)
            {
                return Delete<int>(id);
            }

            /// <summary>
            /// Delete a record from the DB
            /// </summary>
            /// <typeparam name="TId">Type of the ID field</typeparam>
            /// <param name="id"></param>
            /// <returns></returns>
            public bool Delete<TId>(TId id)
            {
                return database.Execute("delete " + TableName + " where Id = @id", new { id }) > 0;
            }

            /// <summary>
            /// Grab a record with a particular Id from the DB 
            /// </summary>
            /// <param name="id"></param>
            /// <returns></returns>
            public T Get(int id)
            {
                return Get<int>(id);
            }
            /// <summary>
            /// Grab a record with a particular Id from the DB
            /// </summary>
            /// <typeparam name="TId">Type of the ID field</typeparam>
            /// <param name="id"></param>
            /// <returns></returns>
            public T Get<TId>(TId id)
            {
                var sql = String.Format(database.SqlCrudTemplate.SelectAllTemplate, TableName, "Id = @id");
                return database.Query<T>(sql, new { id }).FirstOrDefault();
            }

            public virtual T First()
            {
                return database.Query<T>(String.Format(database.SqlCrudTemplate.SelectFirstTemplate, TableName)).FirstOrDefault();
            }

            public IEnumerable<T> All()
            {
                return database.Query<T>(String.Format(database.SqlCrudTemplate.SelectAllTemplate, TableName));
            }

            static ConcurrentDictionary<Type, List<string>> paramNameCache = new ConcurrentDictionary<Type, List<string>>();

            internal static List<string> GetParamNames(object o)
            {
                if (o is DynamicParameters)
                {
                    return (o as DynamicParameters).ParameterNames.ToList();
                }

                List<string> paramNames;
                if (!paramNameCache.TryGetValue(o.GetType(), out paramNames))
                {
                    paramNames = new List<string>();
                    foreach (var prop in o.GetType().GetProperties(BindingFlags.GetProperty | BindingFlags.Instance | BindingFlags.Public))
                    {
                        paramNames.Add(prop.Name);
                    }
                    paramNameCache[o.GetType()] = paramNames;
                }
                return paramNames;
            }
        }

        DbConnection connection;
        int commandTimeout;
        DbTransaction transaction;
        public ICrudTemplate SqlCrudTemplate;

        public static TDatabase Init(DbConnection connection, int commandTimeout)
        {
            TDatabase db = new TDatabase();
            if (db.SqlCrudTemplate == null)
            {
                db.SqlCrudTemplate = new SqlServerCrudTemplate();
            }
            db.InitDatabase(connection, commandTimeout);
            return db;
        }

        internal static Action<TDatabase> tableConstructor;

        internal void InitDatabase(DbConnection connection, int commandTimeout)
        {
            this.connection = connection;
            this.commandTimeout = commandTimeout;
            if (tableConstructor == null)
            {
                tableConstructor = CreateTableConstructorForTable();
            }

            tableConstructor(this as TDatabase);
        }

        internal virtual Action<TDatabase> CreateTableConstructorForTable()
        {
            return CreateTableConstructor(typeof(Table<>));
        }

        public void BeginTransaction(IsolationLevel isolation = IsolationLevel.ReadCommitted)
        {
            transaction = connection.BeginTransaction(isolation);
        }

        public void CommitTransaction()
        {
            transaction.Commit();
            transaction = null;
        }

        public void RollbackTransaction()
        {
            transaction.Rollback();
            transaction = null;
        }

        protected Action<TDatabase> CreateTableConstructor(Type tableType)
        {
            var dm = new DynamicMethod("ConstructInstances", null, new Type[] { typeof(TDatabase) }, true);
            var il = dm.GetILGenerator();

            var setters = GetType().GetProperties()
                .Where(p => p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == tableType)
                .Select(p => Tuple.Create(
                        p.GetSetMethod(true),
                        p.PropertyType.GetConstructor(new Type[] { typeof(TDatabase), typeof(string) }),
                        p.Name,
                        p.DeclaringType
                 ));

            foreach (var setter in setters)
            {
                il.Emit(OpCodes.Ldarg_0);
                // [db]

                il.Emit(OpCodes.Ldstr, setter.Item3);
                // [db, likelyname]

                il.Emit(OpCodes.Newobj, setter.Item2);
                // [table]

                var table = il.DeclareLocal(setter.Item2.DeclaringType);
                il.Emit(OpCodes.Stloc, table);
                // []

                il.Emit(OpCodes.Ldarg_0);
                // [db]

                il.Emit(OpCodes.Castclass, setter.Item4);
                // [db cast to container]

                il.Emit(OpCodes.Ldloc, table);
                // [db cast to container, table]

                il.Emit(OpCodes.Callvirt, setter.Item1);
                // []
            }

            il.Emit(OpCodes.Ret);
            return (Action<TDatabase>)dm.CreateDelegate(typeof(Action<TDatabase>));
        }

        static ConcurrentDictionary<Type, string> tableNameMap = new ConcurrentDictionary<Type, string>();
        private string DetermineTableName<T>(string likelyTableName)
        {
            string name;

            if (!tableNameMap.TryGetValue(typeof(T), out name))
            {
                name = likelyTableName;
                if (!TableExists(name))
                {
                    name = typeof(T).Name;
                }

                tableNameMap[typeof(T)] = name;
            }
            return name;
        }

        private bool TableExists(string name)
        {
            return connection.Query("select 1 from INFORMATION_SCHEMA.TABLES where TABLE_NAME = @name", new { name }, transaction: transaction).Count() == 1;
        }

        public int Execute(string sql, dynamic param = null)
        {
            return SqlMapper.Execute(connection, sql, param as object, transaction, commandTimeout: this.commandTimeout);
        }

        public IEnumerable<T> Query<T>(string sql, dynamic param = null, bool buffered = true)
        {
            return SqlMapper.Query<T>(connection, sql, param as object, transaction, buffered, commandTimeout);
        }

        public IEnumerable<TReturn> Query<TFirst, TSecond, TReturn>(string sql, Func<TFirst, TSecond, TReturn> map, dynamic param = null, IDbTransaction transaction = null, bool buffered = true, string splitOn = "Id", int? commandTimeout = null)
        {
            return SqlMapper.Query(connection, sql, map, param as object, transaction, buffered, splitOn);
        }

        public IEnumerable<TReturn> Query<TFirst, TSecond, TThird, TReturn>(string sql, Func<TFirst, TSecond, TThird, TReturn> map, dynamic param = null, IDbTransaction transaction = null, bool buffered = true, string splitOn = "Id", int? commandTimeout = null)
        {
            return SqlMapper.Query(connection, sql, map, param as object, transaction, buffered, splitOn);
        }

        public IEnumerable<TReturn> Query<TFirst, TSecond, TThird, TFourth, TReturn>(string sql, Func<TFirst, TSecond, TThird, TFourth, TReturn> map, dynamic param = null, IDbTransaction transaction = null, bool buffered = true, string splitOn = "Id", int? commandTimeout = null)
        {
            return SqlMapper.Query(connection, sql, map, param as object, transaction, buffered, splitOn);
        }

        public IEnumerable<TReturn> Query<TFirst, TSecond, TThird, TFourth, TFifth, TReturn>(string sql, Func<TFirst, TSecond, TThird, TFourth, TFifth, TReturn> map, dynamic param = null, IDbTransaction transaction = null, bool buffered = true, string splitOn = "Id", int? commandTimeout = null)
        {
            return SqlMapper.Query(connection, sql, map, param as object, transaction, buffered, splitOn);
        }

        public IEnumerable<dynamic> Query(string sql, dynamic param = null, bool buffered = true)
        {
            return SqlMapper.Query(connection, sql, param as object, transaction, buffered);
        }

        public Dapper.SqlMapper.GridReader QueryMultiple(string sql, dynamic param = null, IDbTransaction transaction = null, int? commandTimeout = null, CommandType? commandType = null)
        {
            return SqlMapper.QueryMultiple(connection, sql, param, transaction, commandTimeout, commandType);
        }


        public void Dispose()
        {
            if (connection.State != ConnectionState.Closed)
            {
                if (transaction != null)
                {
                    transaction.Rollback();
                }

                connection.Close();
                connection = null;
            }
        }
    }


    public interface ICrudTemplate
    {
        string InsertTemplate { get; set; }
        string UpdateTemplate { get; set; }
        string DeleteTemplate { get; set; }
        string SelectAllTemplate { get; set; }
        string SelectFirstTemplate { get; set; }
        string SelectLastInsertedId { get; set; }
    }

    public class SqlServerCrudTemplate : ICrudTemplate
    {
        public string InsertTemplate { get; set; }
        public string UpdateTemplate { get; set; }
        public string DeleteTemplate { get; set; }
        public string SelectAllTemplate { get; set; }
        public string SelectFirstTemplate { get; set; }
        public string SelectLastInsertedId { get; set; }

        public SqlServerCrudTemplate()
        {
            InsertTemplate = "SET NOCOUNT ON INSERT {0} ({1}) VALUES ({2})";
            UpdateTemplate = "UPDATE {0} SET {1} WHERE {2}";
            DeleteTemplate = "DELETE FROM {0} WHERE {1}";
            SelectAllTemplate = "SELECT * FROM {0} WHERE {1}";
            SelectFirstTemplate = "SELECT TOP 1 * FROM {0}";
            SelectLastInsertedId = "SELECT CAST(SCOPE_IDENTITY() AS INT)";
        }
    }

    public class MySqlCrudTemplate : ICrudTemplate
    {
        public string InsertTemplate { get; set; }
        public string UpdateTemplate { get; set; }
        public string DeleteTemplate { get; set; }
        public string SelectAllTemplate { get; set; }
        public string SelectFirstTemplate { get; set; }
        public string SelectLastInsertedId { get; set; }

        public MySqlCrudTemplate()
        {
            InsertTemplate = "INSERT INTO {0} ({1}) VALUES ({2})";
            UpdateTemplate = "UPDATE {0} SET {1} WHERE {2}";
            DeleteTemplate = "DELETE FROM {0} WHERE {1}";
            SelectAllTemplate = "SELECT * FROM {0} WHERE {1}";
            SelectFirstTemplate = "SELECT * FROM {0} LIMIT 1";
            SelectLastInsertedId = "SELECT LAST_INSERT_ID()";
        }
    }
}