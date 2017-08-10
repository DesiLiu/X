﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NewLife.Reflection;
using XCode.Common;

namespace XCode.DataAccessLayer
{
    class Oracle : RemoteDb
    {
        #region 属性
        /// <summary>返回数据库类型。外部DAL数据库类请使用Other</summary>
        public override DatabaseType Type { get { return DatabaseType.Oracle; } }

        private static DbProviderFactory _dbProviderFactory;
        /// <summary>提供者工厂</summary>
        static DbProviderFactory DbProviderFactory
        {
            get
            {
                // 首先尝试使用Oracle.DataAccess
                if (_dbProviderFactory == null)
                {
                    lock (typeof(Oracle))
                    {
                        if (_dbProviderFactory == null)
                        {
                            try
                            {
                                var fileName = "Oracle.ManagedDataAccess.dll";
                                _dbProviderFactory = GetProviderFactory(fileName, "Oracle.ManagedDataAccess.Client.OracleClientFactory");
                                if (_dbProviderFactory != null && DAL.Debug)
                                {
                                    var asm = _dbProviderFactory.GetType().Assembly;
                                    if (DAL.Debug) DAL.WriteLog("Oracle使用文件驱动{0} 版本v{1}", asm.Location, asm.GetName().Version);
                                }
                            }
                            catch (FileNotFoundException) { }
                            catch (Exception ex)
                            {
                                if (DAL.Debug) DAL.WriteLog(ex.ToString());
                            }
                        }

                        // 以下三种方式都可以加载，前两种只是为了减少对程序集的引用，第二种是为了避免第一种中没有注册
                        if (_dbProviderFactory == null)
                        {
                            _dbProviderFactory = DbProviderFactories.GetFactory("System.Data.OracleClient");
                            if (_dbProviderFactory != null && DAL.Debug) DAL.WriteLog("Oracle使用配置驱动{0}", _dbProviderFactory.GetType().Assembly.Location);
                        }
                    }
                }

                return _dbProviderFactory;
            }
        }

        /// <summary>工厂</summary>
        public override DbProviderFactory Factory { get { return DbProviderFactory; } }

        private String _UserID;
        /// <summary>用户名UserID</summary>
        public String UserID
        {
            get
            {
                if (_UserID != null) return _UserID;
                _UserID = String.Empty;

                var connStr = ConnectionString;

                if (String.IsNullOrEmpty(connStr)) return null;

                var ocsb = Factory.CreateConnectionStringBuilder();
                ocsb.ConnectionString = connStr;

                if (ocsb.ContainsKey("User ID")) _UserID = (String)ocsb["User ID"];

                return _UserID;
            }
        }

        protected override void OnSetConnectionString(XDbConnectionStringBuilder builder)
        {
            base.OnSetConnectionString(builder);

            // 获取OCI目录
            if (builder.TryGetAndRemove("DllPath", out var str) && !String.IsNullOrEmpty(str))
            {
            }
        }
        #endregion

        #region 方法
        /// <summary>创建数据库会话</summary>
        /// <returns></returns>
        protected override IDbSession OnCreateSession() { return new OracleSession(this); }

        /// <summary>创建元数据对象</summary>
        /// <returns></returns>
        protected override IMetaData OnCreateMetaData() { return new OracleMeta(); }

        public override Boolean Support(String providerName)
        {
            providerName = providerName.ToLower();
            if (providerName.Contains("oracleclient")) return true;
            if (providerName.Contains("oracle")) return true;

            return false;
        }
        #endregion

        #region 分页
        /// <summary>已重写。获取分页 2012.9.26 HUIYUE修正分页BUG</summary>
        /// <param name="sql">SQL语句</param>
        /// <param name="startRowIndex">开始行，0表示第一行</param>
        /// <param name="maximumRows">最大返回行数，0表示所有行</param>
        /// <param name="keyColumn">主键列。用于not in分页</param>
        /// <returns></returns>
        public override String PageSplit(String sql, Int64 startRowIndex, Int64 maximumRows, String keyColumn)
        {
            // 从第一行开始
            if (startRowIndex <= 0)
            {
                if (maximumRows > 0) sql = String.Format("Select * From ({1}) XCode_T0 Where rownum<={0}", maximumRows, sql);
            }
            else
            {
                if (maximumRows <= 0)
                    sql = String.Format("Select * From ({1}) XCode_T0 Where rownum>={0}", startRowIndex + 1, sql);
                else
                    sql = String.Format("Select * From (Select XCode_T0.*, rownum as rowNumber From ({1}) XCode_T0 Where rownum<={2}) XCode_T1 Where rowNumber>{0}", startRowIndex, sql, startRowIndex + maximumRows);
            }
            return sql;
        }

        /// <summary>构造分页SQL</summary>
        /// <remarks>
        /// 两个构造分页SQL的方法，区别就在于查询生成器能够构造出来更好的分页语句，尽可能的避免子查询。
        /// MS体系的分页精髓就在于唯一键，当唯一键带有Asc/Desc/Unkown等排序结尾时，就采用最大最小值分页，否则使用较次的TopNotIn分页。
        /// TopNotIn分页和MaxMin分页的弊端就在于无法完美的支持GroupBy查询分页，只能查到第一页，往后分页就不行了，因为没有主键。
        /// </remarks>
        /// <param name="builder">查询生成器</param>
        /// <param name="startRowIndex">开始行，0表示第一行</param>
        /// <param name="maximumRows">最大返回行数，0表示所有行</param>
        /// <returns>分页SQL</returns>
        public override SelectBuilder PageSplit(SelectBuilder builder, Int64 startRowIndex, Int64 maximumRows)
        {
            // 从第一行开始，不需要分页
            if (startRowIndex <= 0)
            {
                if (maximumRows > 0) builder = builder.AsChild("XCode_T0", false).AppendWhereAnd("rownum<={0}", maximumRows);
                return builder;
            }
            if (maximumRows < 1) return builder.AsChild("XCode_T0", false).AppendWhereAnd("rownum>={0}", startRowIndex + 1);

            builder = builder.AsChild("XCode_T0", false).AppendWhereAnd("rownum<={0}", startRowIndex + maximumRows);
            builder.Column = "XCode_T0.*, rownum as rowNumber";
            builder = builder.AsChild("XCode_T1", false).AppendWhereAnd("rowNumber>{0}", startRowIndex);

            return builder;
        }
        #endregion

        #region 数据库特性
        /// <summary>已重载。格式化时间</summary>
        /// <param name="dateTime"></param>
        /// <returns></returns>
        public override String FormatDateTime(DateTime dateTime) { return "To_Date('" + dateTime.ToFullString() + "', 'YYYY-MM-DD HH24:MI:SS')"; }

        public override String FormatValue(IDataColumn field, Object value)
        {
            var code = System.Type.GetTypeCode(field.DataType);
            var isNullable = field.Nullable;

            if (code == TypeCode.String)
            {
                if (value == null) return isNullable ? "null" : "''";

                if (field.RawType.StartsWithIgnoreCase("n"))
                    return "N'" + value.ToString().Replace("'", "''") + "'";
                else
                    return "'" + value.ToString().Replace("'", "''") + "'";
            }

            return base.FormatValue(field, value);
        }

        /// <summary>格式化标识列，返回插入数据时所用的表达式，如果字段本身支持自增，则返回空</summary>
        /// <param name="field">字段</param>
        /// <param name="value">数值</param>
        /// <returns></returns>
        public override String FormatIdentity(IDataColumn field, Object value)
        {
            return String.Format("SEQ_{0}.nextval", field.Table.TableName);
        }

        internal protected override String ParamPrefix { get { return ":"; } }

        /// <summary>字符串相加</summary>
        /// <param name="left"></param>
        /// <param name="right"></param>
        /// <returns></returns>
        public override String StringConcat(String left, String right) { return (!String.IsNullOrEmpty(left) ? left : "\'\'") + "||" + (!String.IsNullOrEmpty(right) ? right : "\'\'"); }

        /// <summary>创建参数</summary>
        /// <param name="name">名称</param>
        /// <param name="value">值</param>
        /// <param name="type">类型</param>
        /// <returns></returns>
        public override IDataParameter CreateParameter(String name, Object value, Type type = null)
        {
            if (type == null) type = value?.GetType();

            if (type == typeof(Boolean))
            {
                type = typeof(Int32);
                value = value.ToBoolean() ? 1 : 0;
            }

            var dp = base.CreateParameter(name, value, type);

            // 修正时间映射
            if (type == typeof(DateTime)) dp.DbType = DbType.Date;

            return dp;
        }
        #endregion

        #region 关键字
        protected override String ReservedWordsStr
        {
            get { return "Sort,Level,ALL,ALTER,AND,ANY,AS,ASC,BETWEEN,BY,CHAR,CHECK,CLUSTER,COMPRESS,CONNECT,CREATE,DATE,DECIMAL,DEFAULT,DELETE,DESC,DISTINCT,DROP,ELSE,EXCLUSIVE,EXISTS,FLOAT,FOR,FROM,GRANT,GROUP,HAVING,IDENTIFIED,IN,INDEX,INSERT,INTEGER,INTERSECT,INTO,IS,LIKE,LOCK,LONG,MINUS,MODE,NOCOMPRESS,NOT,NOWAIT,NULL,NUMBER,OF,ON,OPTION,OR,ORDER,PCTFREE,PRIOR,PUBLIC,RAW,RENAME,RESOURCE,REVOKE,SELECT,SET,SHARE,SIZE,SMALLINT,START,SYNONYM,TABLE,THEN,TO,TRIGGER,UNION,UNIQUE,UPDATE,VALUES,VARCHAR,VARCHAR2,VIEW,WHERE,WITH,User,Online"; }
        }

        /// <summary>格式化关键字</summary>
        /// <param name="keyWord">表名</param>
        /// <returns></returns>
        public override String FormatKeyWord(String keyWord)
        {
            //return String.Format("\"{0}\"", keyWord);

            //if (String.IsNullOrEmpty(keyWord)) throw new ArgumentNullException("keyWord");
            if (String.IsNullOrEmpty(keyWord)) return keyWord;

            var pos = keyWord.LastIndexOf(".");

            if (pos < 0) return "\"" + keyWord + "\"";

            var tn = keyWord.Substring(pos + 1);
            if (tn.StartsWith("\"")) return keyWord;

            return keyWord.Substring(0, pos + 1) + "\"" + tn + "\"";
        }

        /// <summary>是否忽略大小写，如果不忽略则在表名字段名外面加上双引号</summary>
        static Boolean _IgnoreCase = Setting.Current.Oracle.IgnoreCase;

        public override String FormatName(String name)
        {
            if (_IgnoreCase)
                return base.FormatName(name);
            else
                return FormatKeyWord(name);
        }
        #endregion

        #region 辅助
        Dictionary<String, DateTime> cache = new Dictionary<String, DateTime>();
        public Boolean NeedAnalyzeStatistics(String tableName)
        {
            var owner = Owner;
            if (owner.IsNullOrEmpty()) owner = UserID;

            // 非当前用户，不支持统计
            if (!owner.EqualIgnoreCase(UserID)) return false;

            var key = String.Format("{0}.{1}", owner, tableName);
            if (!cache.TryGetValue(key, out var dt))
            {
                dt = DateTime.MinValue;
                cache[key] = dt;
            }

            if (dt > DateTime.Now) return false;

            // 一分钟后才可以再次分析
            dt = DateTime.Now.AddSeconds(10);
            cache[key] = dt;

            return true;
        }
        #endregion
    }

    /// <summary>Oracle数据库</summary>
    internal class OracleSession : RemoteDbSession
    {
        static OracleSession()
        {
            // 旧版Oracle运行时会因为没有这个而报错
            var name = "NLS_LANG";
            if (String.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))) Environment.SetEnvironmentVariable(name, "SIMPLIFIED CHINESE_CHINA.ZHS16GBK");
        }

        #region 构造函数
        public OracleSession(IDatabase db) : base(db) { }
        #endregion

        #region 基本方法 查询/执行
        /// <summary>快速查询单表记录数，稍有偏差</summary>
        /// <param name="tableName"></param>
        /// <returns></returns>
        public override Int64 QueryCountFast(String tableName)
        {
            if (String.IsNullOrEmpty(tableName)) return 0;

            var p = tableName.LastIndexOf(".");
            if (p >= 0 && p < tableName.Length - 1) tableName = tableName.Substring(p + 1);
            tableName = tableName.ToUpper();

            var owner = (Database as Oracle).Owner;
            if (owner.IsNullOrEmpty()) owner = (Database as Oracle).UserID;
            //var owner = (Database as Oracle).Owner.ToUpper();
            owner = owner.ToUpper();

            //if ((Database as Oracle).NeedAnalyzeStatistics(tableName))
            //{
            //    // 异步更新，屏蔽错误
            //    Task.Run(() =>
            //    {
            //        try
            //        {
            //            Execute("analyze table {0}.{1} compute statistics".F(owner, tableName));
            //        }
            //        catch { }
            //    });
            //}

            var sql = String.Format("select NUM_ROWS from sys.all_indexes where TABLE_OWNER='{0}' and TABLE_NAME='{1}' and UNIQUENESS='UNIQUE'", owner, tableName);
            return ExecuteScalar<Int64>(sql);
        }

        static Regex reg_SEQ = new Regex(@"\b(\w+)\.nextval\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        /// <summary>执行插入语句并返回新增行的自动编号</summary>
        /// <param name="sql">SQL语句</param>
        /// <param name="type">命令类型，默认SQL文本</param>
        /// <param name="ps">命令参数</param>
        /// <returns>新增行的自动编号</returns>
        public override Int64 InsertAndGetIdentity(String sql, CommandType type = CommandType.Text, params IDataParameter[] ps)
        {
            BeginTransaction(IsolationLevel.Serializable);
            try
            {
                Int64 rs = Execute(sql, type, ps);
                if (rs > 0)
                {
                    var m = reg_SEQ.Match(sql);
                    if (m != null && m.Success && m.Groups != null && m.Groups.Count > 0)
                        rs = ExecuteScalar<Int64>(String.Format("Select {0}.currval From dual", m.Groups[1].Value));
                }
                Commit();
                return rs;
            }
            catch { Rollback(true); throw; }
            finally
            {
                AutoClose();
            }
        }
        #endregion
    }

    /// <summary>Oracle元数据</summary>
    class OracleMeta : RemoteDbMetaData
    {
        public OracleMeta()
        {
            Types = _DataTypes;
        }

        /// <summary>拥有者</summary>
        public String Owner
        {
            get
            {
                var owner = Database.Owner;
                if (owner.IsNullOrEmpty()) owner = (Database as Oracle).UserID;

                return owner.ToUpper();
            }
        }

        /// <summary>用户名</summary>
        public String UserID { get { return (Database as Oracle).UserID.ToUpper(); } }

        /// <summary>取得所有表构架</summary>
        /// <returns></returns>
        protected override List<IDataTable> OnGetTables(String[] names)
        {
            DataTable dt = null;

            // 采用集合过滤，提高效率
            String tableName = null;
            if (names != null && names.Length == 1) tableName = names.FirstOrDefault();
            if (String.IsNullOrEmpty(tableName))
                tableName = null;
            else
            {
                // 不缺分大小写，并且不是保留字，才转大写
                if (Setting.Current.Oracle.IgnoreCase && !(Database as Oracle).IsReservedWord(tableName))
                    tableName = tableName.ToUpper();
            }

            var owner = Owner;
            //if (owner.IsNullOrEmpty()) owner = UserID;

            dt = GetSchema(_.Tables, new String[] { owner, tableName });
            if (!dt.Columns.Contains("TABLE_TYPE"))
            {
                dt.Columns.Add("TABLE_TYPE", typeof(String));
                foreach (var dr in dt.Rows?.ToArray())
                {
                    dr["TABLE_TYPE"] = "Table";
                }
            }
            var dtView = GetSchema(_.Views, new String[] { owner, tableName });
            if (dtView != null && dtView.Rows.Count != 0)
            {
                foreach (var dr in dtView.Rows?.ToArray())
                {
                    var drNew = dt.NewRow();
                    drNew["OWNER"] = dr["OWNER"];
                    drNew["TABLE_NAME"] = dr["VIEW_NAME"];
                    drNew["TABLE_TYPE"] = "View";
                    dt.Rows.Add(drNew);
                }
            }

            var columns = GetSchema(_.Columns, new String[] { owner, tableName, null });
            var indexes = GetSchema(_.Indexes, new String[] { owner, null, owner, tableName });
            var indexColumns = GetSchema(_.IndexColumns, new String[] { owner, null, owner, tableName, null });

            // 主键
            if (MetaDataCollections.Contains(_.PrimaryKeys)) _PrimaryKeys = GetSchema(_.PrimaryKeys, new String[] { owner, tableName, null });

            // 序列
            var sql = "Select * From ALL_SEQUENCES Where SEQUENCE_OWNER='{0}'".F(owner);
            dtSequences = Database.CreateSession().Query(sql).Tables[0];

            // 表注释
            sql = "Select * From ALL_TAB_COMMENTS Where Owner='{0}'".F(owner);
            if (!tableName.IsNullOrEmpty()) sql += " And TABLE_NAME='{0}'".F(tableName);
            dtTableComment = Database.CreateSession().Query(sql).Tables[0];

            // 列注释
            sql = "Select * From ALL_COL_COMMENTS Where Owner='{0}'".F(owner);
            if (!tableName.IsNullOrEmpty()) sql += " And TABLE_NAME='{0}'".F(tableName);
            dtColumnComment = Database.CreateSession().Query("Select * From ALL_COL_COMMENTS where Owner='{0}'".F(owner)).Tables[0];

            var list = GetTables(dt.Rows.ToArray(), names, columns, indexes, indexColumns);

            _PrimaryKeys = null;
            dtSequences = null;
            dtTableComment = null;
            dtColumnComment = null;

            return list;
        }

        private DataTable _PrimaryKeys;
        protected override void FixTable(IDataTable table, DataRow dr)
        {
            base.FixTable(table, dr);

            // 主键
            var dt = _PrimaryKeys;
            if (dt != null && dt.Rows.Count > 0)
            {
                // 找到主键所在索引，这个索引的列才是主键
                if (TryGetDataRowValue(dt.Rows[0], _.IndexName, out String name) && !String.IsNullOrEmpty(name))
                {
                    var di = table.Indexes.FirstOrDefault(i => i.Name == name);
                    if (di != null)
                    {
                        di.PrimaryKey = true;
                        foreach (var dc in table.Columns)
                        {
                            dc.PrimaryKey = di.Columns.Contains(dc.ColumnName);
                        }
                    }
                }
            }

            // 表注释 USER_TAB_COMMENTS
            table.Description = GetTableComment(table.TableName);

            if (table?.Columns == null || table.Columns.Count == 0) return;

            // 检查该表是否有序列
            if (CheckSeqExists("SEQ_{0}".F(table.TableName)))
            {
                // 不好判断自增列表，只能硬编码
                var dc = table.GetColumn("ID");
                if (dc == null) dc = table.Columns.FirstOrDefault(e => e.PrimaryKey && e.DataType.IsInt());
                if (dc != null) dc.Identity = true;
            }
        }

        /// <summary>序列</summary>
        DataTable dtSequences;
        /// <summary>检查序列是否存在</summary>
        /// <param name="name">名称</param>
        /// <returns></returns>
        Boolean CheckSeqExists(String name)
        {
            if (dtSequences?.Rows == null || dtSequences.Rows.Count < 1) return false;

            var where = String.Format("SEQUENCE_NAME='{0}'", name);
            var drs = dtSequences.Select(where);
            return drs != null && drs.Length > 0;
        }

        DataTable dtTableComment;
        String GetTableComment(String name)
        {
            if (dtTableComment?.Rows == null || dtTableComment.Rows.Count < 1) return null;

            var where = String.Format("TABLE_NAME='{0}'", name);
            var drs = dtTableComment.Select(where);
            if (drs != null && drs.Length > 0) return Convert.ToString(drs[0]["COMMENTS"]);

            return null;
        }

        /// <summary>取得指定表的所有列构架</summary>
        /// <param name="table"></param>
        /// <param name="columns">列</param>
        /// <returns></returns>
        protected override List<IDataColumn> GetFields(IDataTable table, DataTable columns)
        {
            var list = base.GetFields(table, columns);
            if (list == null || list.Count < 1) return null;

            // 字段注释
            if (list != null && list.Count > 0)
            {
                foreach (var field in list)
                {
                    field.Description = GetColumnComment(table.TableName, field.ColumnName);
                }
            }

            return list;
        }

        const String KEY_OWNER = "OWNER";

        protected override List<IDataColumn> GetFields(IDataTable table, DataRow[] rows)
        {
            if (rows == null || rows.Length < 1) return null;

            var owner = Owner;
            if (owner.IsNullOrEmpty() || !rows[0].Table.Columns.Contains(KEY_OWNER)) return base.GetFields(table, rows);

            var list = new List<DataRow>();
            foreach (var dr in rows)
            {
                if (TryGetDataRowValue(dr, KEY_OWNER, out String str) && owner.EqualIgnoreCase(str)) list.Add(dr);
            }

            return base.GetFields(table, list.ToArray());
        }

        DataTable dtColumnComment;
        String GetColumnComment(String tableName, String columnName)
        {
            var dt = dtColumnComment;
            if (dt?.Rows == null || dt.Rows.Count < 1) return null;

            var where = String.Format("{0}='{1}' AND {2}='{3}'", _.TalbeName, tableName, _.ColumnName, columnName);
            var drs = dt.Select(where);
            if (drs != null && drs.Length > 0) return Convert.ToString(drs[0]["COMMENTS"]);
            return null;
        }

        protected override void FixField(IDataColumn field, DataRow drColumn)
        {
            base.FixField(field, drColumn);

            // 处理数字类型
            if (field.RawType.StartsWithIgnoreCase("NUMBER") && field is XField fi)
            {
                var prec = fi.Precision;
                if (fi.Scale == 0)
                {
                    // 0表示长度不限制，为了方便使用，转为最常见的Int32
                    if (prec == 0)
                        field.DataType = typeof(Int32);
                    else if (prec == 1)
                        field.DataType = typeof(Boolean);
                    else if (prec <= 5)
                        field.DataType = typeof(Int16);
                    else if (prec <= 10)
                        field.DataType = typeof(Int32);
                    else
                        field.DataType = typeof(Int64);
                }
                else
                {
                    if (prec == 0)
                        field.DataType = typeof(Decimal);
                    else if (prec <= 5)
                        field.DataType = typeof(Single);
                    else if (prec <= 10)
                        field.DataType = typeof(Double);
                }
                if (prec > 0 && field.RawType.EqualIgnoreCase("NUMBER")) field.RawType += "({0},{1})".F(prec, fi.Scale);
            }

            // 长度
            if (TryGetDataRowValue(drColumn, "LENGTHINCHARS", out Int32 len) && len > 0) field.Length = len;
        }

        protected override String GetFieldType(IDataColumn field)
        {
            var precision = 0;
            var scale = 0;

            if (field is XField fi)
            {
                precision = fi.Precision;
                scale = fi.Scale;
            }

            switch (Type.GetTypeCode(field.DataType))
            {
                case TypeCode.Boolean:
                    return "NUMBER(1, 0)";
                case TypeCode.Int16:
                case TypeCode.UInt16:
                    if (precision <= 0) precision = 5;
                    return String.Format("NUMBER({0}, 0)", precision);
                case TypeCode.Int32:
                case TypeCode.UInt32:
                    //if (precision <= 0) precision = 10;
                    if (precision <= 0) return "NUMBER";
                    return String.Format("NUMBER({0}, 0)", precision);
                case TypeCode.Int64:
                case TypeCode.UInt64:
                    if (precision <= 0) precision = 20;
                    return String.Format("NUMBER({0}, 0)", precision);
                case TypeCode.Single:
                    if (precision <= 0) precision = 5;
                    if (scale <= 0) scale = 1;
                    return String.Format("NUMBER({0}, {1})", precision, scale);
                case TypeCode.Double:
                    if (precision <= 0) precision = 10;
                    if (scale <= 0) scale = 2;
                    return String.Format("NUMBER({0}, {1})", precision, scale);
                case TypeCode.Decimal:
                    if (precision <= 0) precision = 20;
                    if (scale <= 0) scale = 4;
                    return String.Format("NUMBER({0}, {1})", precision, scale);
                default:
                    break;
            }

            return base.GetFieldType(field);
        }

        protected override void FixIndex(IDataIndex index, DataRow dr)
        {
            if (TryGetDataRowValue(dr, "UNIQUENESS", out String str))
                index.Unique = str == "UNIQUE";

            base.FixIndex(index, dr);
        }

        /// <summary>数据类型映射</summary>
        private static Dictionary<Type, String[]> _DataTypes = new Dictionary<Type, String[]>
        {
            { typeof(Byte[]), new String[] { "RAW({0})", "BFILE", "BLOB", "LONG RAW" } },
            { typeof(Boolean), new String[] { "NUMBER(1,0)" } },
            { typeof(Byte), new String[] { "NUMBER(1,0)" } },
            { typeof(Int16), new String[] { "NUMBER(5,0)" } },
            { typeof(Int32), new String[] { "NUMBER(10,0)" } },
            { typeof(Int64), new String[] { "NUMBER(20,0)" } },
            { typeof(Single), new String[] { "BINARY_FLOAT" } },
            { typeof(Double), new String[] { "BINARY_DOUBLE" } },
            { typeof(Decimal), new String[] { "NUMBER", "FLOAT({0})" } },
            { typeof(DateTime), new String[] { "DATE", "TIMESTAMP({0})", "TIMESTAMP({0} WITH LOCAL TIME ZONE)", "TIMESTAMP({0} WITH TIME ZONE)" } },
            { typeof(String), new String[] { "NVARCHAR2({0})", "LONG", "VARCHAR2({0})", "CHAR({0})", "CLOB", "NCHAR({0})", "NCLOB", "XMLTYPE", "ROWID" } }
        };

        #region 架构定义
        public override Object SetSchema(DDLSchema schema, params Object[] values)
        {
            var session = Database.CreateSession();

            var dbname = String.Empty;
            var databaseName = String.Empty;
            switch (schema)
            {
                case DDLSchema.DatabaseExist:
                    // Oracle不支持判断数据库是否存在
                    return true;

                default:
                    break;
            }
            return base.SetSchema(schema, values);
        }

        public override String DatabaseExistSQL(String dbname)
        {
            return String.Empty;
        }

        protected override String GetFieldConstraints(IDataColumn field, Boolean onlyDefine)
        {
            if (field.Nullable)
                return " NULL";
            else
                return " NOT NULL";
        }

        public override String CreateTableSQL(IDataTable table)
        {
            var fs = new List<IDataColumn>(table.Columns);

            var sb = new StringBuilder(32 + fs.Count * 20);

            sb.AppendFormat("Create Table {0}(", FormatName(table.TableName));
            for (var i = 0; i < fs.Count; i++)
            {
                sb.AppendLine();
                sb.Append("\t");
                sb.Append(FieldClause(fs[i], true));
                if (i < fs.Count - 1) sb.Append(",");
            }

            // 主键
            var pks = table.PrimaryKeys;
            if (pks != null && pks.Length > 0)
            {
                sb.AppendLine(",");
                sb.Append("\t");
                sb.AppendFormat("constraint pk_{0} primary key (", table.TableName);
                for (var i = 0; i < pks.Length; i++)
                {
                    if (i > 0) sb.Append(",");
                    sb.Append(FormatName(pks[i].ColumnName));
                }
                sb.Append(")");
            }

            sb.AppendLine();
            sb.Append(")");

            var sql = sb.ToString();
            if (String.IsNullOrEmpty(sql)) return sql;

            // 感谢@晴天（412684802）和@老徐（gregorius 279504479），这里的最小值开始必须是0，插入的时候有++i的效果，才会得到从1开始的编号
            var sqlSeq = String.Format("Create Sequence SEQ_{0} Minvalue 0 Maxvalue 9999999999 Start With 0 Increment By 1 Cache 20", table.TableName);
            //return sql + "; " + Environment.NewLine + sqlSeq;
            // 去掉分号后的空格，Oracle不支持同时执行多个语句
            return sql + ";" + Environment.NewLine + sqlSeq;
        }

        public override String DropTableSQL(String tableName)
        {
            var sql = base.DropTableSQL(tableName);
            if (String.IsNullOrEmpty(sql)) return sql;

            var sqlSeq = String.Format("Drop Sequence SEQ_{0}", tableName);
            return sql + "; " + Environment.NewLine + sqlSeq;
        }

        public override String AddColumnSQL(IDataColumn field)
        {
            var owner = Owner;
            if (owner.EqualIgnoreCase(UserID))
                return String.Format("Alter Table {0} Add {1}", FormatName(field.Table.TableName), FieldClause(field, true));
            else
                return String.Format("Alter Table {2}.{0} Add {1}", FormatName(field.Table.TableName), FieldClause(field, true), owner);
        }

        public override String AlterColumnSQL(IDataColumn field, IDataColumn oldfield)
        {
            var owner = Owner;
            if (owner.EqualIgnoreCase(UserID))
                return String.Format("Alter Table {0} Modify {1}", FormatName(field.Table.TableName), FieldClause(field, false));
            else
                return String.Format("Alter Table {2}.{0} Modify {1}", FormatName(field.Table.TableName), FieldClause(field, false), owner);
        }

        public override String DropColumnSQL(IDataColumn field)
        {
            var owner = Owner;
            if (owner.EqualIgnoreCase(UserID))
                return String.Format("Alter Table {0} Drop Column {1}", FormatName(field.Table.TableName), field.ColumnName);
            else
                return String.Format("Alter Table {2}.{0} Drop Column {1}", FormatName(field.Table.TableName), field.ColumnName, owner);
        }

        public override String AddTableDescriptionSQL(IDataTable table)
        {
            //return String.Format("Update USER_TAB_COMMENTS Set COMMENTS='{0}' Where TABLE_NAME='{1}'", table.Description, table.Name);

            return String.Format("Comment On Table {0} is '{1}'", FormatName(table.TableName), table.Description);
        }

        public override String DropTableDescriptionSQL(IDataTable table)
        {
            //return String.Format("Update USER_TAB_COMMENTS Set COMMENTS='' Where TABLE_NAME='{0}'", table.Name);

            return String.Format("Comment On Table {0} is ''", FormatName(table.TableName));
        }

        public override String AddColumnDescriptionSQL(IDataColumn field)
        {
            //return String.Format("Update USER_COL_COMMENTS Set COMMENTS='{0}' Where TABLE_NAME='{1}' AND COLUMN_NAME='{2}'", field.Description, field.Table.Name, field.Name);

            return String.Format("Comment On Column {0}.{1} is '{2}'", FormatName(field.Table.TableName), FormatName(field.ColumnName), field.Description);
        }

        public override String DropColumnDescriptionSQL(IDataColumn field)
        {
            //return String.Format("Update USER_COL_COMMENTS Set COMMENTS='' Where TABLE_NAME='{0}' AND COLUMN_NAME='{1}'", field.Table.Name, field.Name);

            return String.Format("Comment On Column {0}.{1} is ''", FormatName(field.Table.TableName), FormatName(field.ColumnName));
        }
        #endregion
    }
}