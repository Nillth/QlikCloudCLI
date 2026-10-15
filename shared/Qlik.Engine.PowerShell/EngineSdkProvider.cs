using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Qlik.Engine.PowerShell
{
    // No SDK types occur in this class's method signatures or base types. These
    // method bodies are reached only after an Engine command is invoked.
    internal sealed class EngineSdkProvider : IEngineProvider
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public object GetSession(object app) { return ((global::Qlik.Engine.IApp)app).Session; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool IsClosed(object app, object session)
        {
            var connection = (global::Qlik.Engine.Communication.IQlikConnection)session;
            var concrete = app as global::Qlik.Engine.QixClassBase;
            return connection.CancellationToken.IsCancellationRequested || (concrete != null && concrete.IsClosed);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void DisposeApp(object app, object session)
        {
            try { if (session is IDisposable) ((IDisposable)session).Dispose(); }
            finally { try { ((IDisposable)app).Dispose(); } catch { } }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public async Task<EngineReadResult> ReadAsync(object appObject, EngineReadRequest request)
        {
            var app = (global::Qlik.Engine.IApp)appObject;
            switch (request.Operation)
            {
                case "AppLayout": return Result(Snapshot(await app.GetAppLayoutAsync().ConfigureAwait(false)));
                case "Script": return Result(await app.GetScriptAsync().ConfigureAwait(false));
                case "TestScript":
                    var diagnostics = (await app.CheckScriptSyntaxAsync().ConfigureAwait(false)).ToArray();
                    return Result(Object("IsValid", diagnostics.Length == 0, "Diagnostics", Snapshot(diagnostics)));
                case "TestExpression":
                    var checkedExpression = await app.CheckExpressionAsync(request.Expression, null).ConfigureAwait(false);
                    return Result(Object("IsValid", string.IsNullOrEmpty(checkedExpression.ErrorMsg) && (checkedExpression.BadFieldNames == null || !checkedExpression.BadFieldNames.Any()),
                        "ErrorMessage", checkedExpression.ErrorMsg, "BadFieldNames", Snapshot(checkedExpression.BadFieldNames),
                        "DangerousFieldNames", Snapshot(checkedExpression.DangerousFieldNames)));
                case "Expression":
                    var value = await app.EvaluateExAsync(request.Expression).ConfigureAwait(false);
                    return Result(Object("Text", value.Text, "Number", value.Number, "IsNumeric", value.IsNumeric));
                case "Field":
                    if (string.IsNullOrEmpty(request.Name)) return await ReadListAsync(appObject, "FieldList", false).ConfigureAwait(false);
                    using (var field = await app.GetFieldAsync(request.Name, request.State ?? "$").ConfigureAwait(false))
                    {
                        var description = await app.GetFieldDescriptionAsync(request.Name).ConfigureAwait(false);
                        var properties = await field.GetNxPropertiesAsync().ConfigureAwait(false);
                        int cardinality = await field.GetCardinalAsync().ConfigureAwait(false);
                        return Result(Object("Name", request.Name, "State", request.State ?? "$", "Cardinality", cardinality,
                            "Description", Snapshot(description), "Properties", Snapshot(properties)));
                    }
                case "Table":
                    var tables = await app.GetTablesAndKeysAsync(new global::Qlik.Engine.Size { cx = 1000, cy = 1000 },
                        new global::Qlik.Engine.Size { cx = 0, cy = 0 }, 0, false, false, false).ConfigureAwait(false);
                    var rows = new List<object>();
                    foreach (var table in tables.Tr ?? Enumerable.Empty<global::Qlik.Engine.TableRecord>())
                    {
                        if (request.Name != null && !string.Equals(request.Name, table.Name, StringComparison.OrdinalIgnoreCase)) continue;
                        var snapshot = (PSObject)Snapshot(table);
                        var keys = (tables.K ?? Enumerable.Empty<global::Qlik.Engine.SourceKeyRecord>())
                            .Where(key => key.Tables != null && key.Tables.Contains(table.Name, StringComparer.Ordinal)).ToArray();
                        snapshot.Properties.Add(new PSNoteProperty("Keys", Snapshot(keys)));
                        rows.Add(snapshot);
                    }
                    return Result(rows.ToArray(), true);
                default: return await ReadListAsync(appObject, request.Operation, request.AsSdkObject).ConfigureAwait(false);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private async Task<EngineReadResult> ReadListAsync(object appObject, string kind, bool asSdkObject)
        {
            object list = null;
            bool returned = false;
            try
            {
                var app = (global::Qlik.Engine.IApp)appObject;
                switch (kind)
                {
                    case "SheetList": list = await global::Qlik.Sense.Client.AppExtensions.GetSheetListAsync(app).ConfigureAwait(false); break;
                    case "BookmarkList": list = await global::Qlik.Sense.Client.AppExtensions.GetBookmarkListAsync(app).ConfigureAwait(false); break;
                    case "DimensionList": list = await global::Qlik.Sense.Client.AppExtensions.GetDimensionListAsync(app).ConfigureAwait(false); break;
                    case "MeasureList": list = await global::Qlik.Sense.Client.AppExtensions.GetMeasureListAsync(app).ConfigureAwait(false); break;
                    case "MasterObjectList": list = await global::Qlik.Sense.Client.AppExtensions.GetMasterObjectListAsync(app).ConfigureAwait(false); break;
                    case "FieldList": list = await global::Qlik.Sense.Client.AppExtensions.GetFieldListAsync(app).ConfigureAwait(false); break;
                    case "VariableList": list = await global::Qlik.Sense.Client.AppExtensions.GetVariableListAsync(app).ConfigureAwait(false); break;
                    default: throw new EngineOperationException("Unsupported Engine read operation.");
                }
                if (asSdkObject)
                {
                    returned = true;
                    return new EngineReadResult { Value = list, NativeChild = list };
                }
                var layout = await ((global::Qlik.Engine.IGenericObject)list).GetLayoutAsync().ConfigureAwait(false);
                JObject json = JObject.Parse(((global::Qlik.Engine.IAbstractStructure)layout).PrintStructure(Formatting.None));
                string container = kind == "SheetList" || kind == "MasterObjectList" ? "qAppObjectList" : "q" + kind;
                var items = json[container]?["qItems"] as JArray;
                if (items == null) throw new EngineOperationException("The Engine list layout did not contain the expected item array.");
                return Result(items.Select(SnapshotToken).ToArray(), true);
            }
            finally
            {
                if (list != null && !returned) await DestroyChildAsync(appObject, list).ConfigureAwait(false);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public async Task DestroyChildAsync(object appObject, object child)
        {
            try
            {
                var app = (global::Qlik.Engine.IApp)appObject;
                if (!app.Session.CancellationToken.IsCancellationRequested)
                    await app.DestroyGenericSessionObjectAsync(((global::Qlik.Engine.IQixClassBase)child).Id).ConfigureAwait(false);
            }
            finally { ((IDisposable)child).Dispose(); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static object Snapshot(object value)
        {
            if (value == null) return null;
            var structure = value as global::Qlik.Engine.IAbstractStructure;
            if (structure != null) return SnapshotToken(JToken.Parse(structure.PrintStructure(Formatting.None)));
            if (value is string || value.GetType().IsValueType) return value;
            if (value is IEnumerable) return ((IEnumerable)value).Cast<object>().Select(Snapshot).ToArray();
            var result = new PSObject();
            foreach (PropertyInfo property in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanRead || property.GetIndexParameters().Length != 0 || property.Name == "Session") continue;
                result.Properties.Add(new PSNoteProperty(property.Name, Snapshot(property.GetValue(value, null))));
            }
            return result;
        }

        private static object SnapshotToken(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined) return null;
            if (token is JValue) return ((JValue)token).Value;
            if (token is JArray) return ((JArray)token).Select(SnapshotToken).ToArray();
            var source = (JObject)token;
            var result = new PSObject();
            foreach (JProperty property in source.Properties())
            {
                string name = property.Name.Length > 1 && property.Name[0] == 'q' && char.IsUpper(property.Name[1]) ? property.Name.Substring(1) : property.Name;
                if (result.Properties[name] != null) name = property.Name;
                string candidate = name;
                for (int suffix = 2; result.Properties[name] != null; suffix++) name = candidate + "_" + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
                result.Properties.Add(new PSNoteProperty(name, SnapshotToken(property.Value)));
            }
            AddAlias(result, "Id", source.SelectToken("qInfo.qId"));
            AddAlias(result, "ObjectType", source.SelectToken("qInfo.qType"));
            AddAlias(result, "Title", source.SelectToken("qMeta.title") ?? source.SelectToken("qMeta.qTitle") ?? source.SelectToken("qData.title") ?? source.SelectToken("qData.qTitle"));
            AddAlias(result, "Description", source.SelectToken("qMeta.description") ?? source.SelectToken("qMeta.qDescription") ?? source.SelectToken("qData.description") ?? source.SelectToken("qData.qDescription"));
            return result;
        }

        private static void AddAlias(PSObject result, string name, JToken token)
        {
            if (result.Properties[name] == null && token != null) result.Properties.Add(new PSNoteProperty(name, SnapshotToken(token)));
        }

        private static PSObject Object(params object[] pairs)
        {
            var result = new PSObject();
            for (int index = 0; index < pairs.Length; index += 2) result.Properties.Add(new PSNoteProperty((string)pairs[index], pairs[index + 1]));
            return result;
        }

        private static EngineReadResult Result(object value, bool enumerate = false)
        { return new EngineReadResult { Value = value, Enumerate = enumerate }; }
    }
}
