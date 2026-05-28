using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace RevitMCP;

/// <summary>
/// IExternalEventHandler runs on Revit's UI thread.
/// All Revit API calls MUST happen here.
/// </summary>
public class RevitEventHandler : IExternalEventHandler
{
    public sealed class Request
    {
        public Request(string action, JObject payload)
        {
            Action = action;
            Payload = payload;
        }

        public string Action { get; }
        public JObject Payload { get; }
        public JObject? Result { get; set; }
        public string? Error { get; set; }
        public ManualResetEventSlim Done { get; } = new(false);
    }

    private readonly object _sync = new();
    private Request? _pendingRequest;

    public Request SetRequest(string action, JObject payload)
    {
        var request = new Request(action, payload);
        lock (_sync)
            _pendingRequest = request;
        return request;
    }

    public static bool WaitForResult(Request request, TimeSpan timeout) => request.Done.Wait(timeout);

    public static (JObject? result, string? error) GetResult(Request request) => (request.Result, request.Error);

    public string GetName() => "RevitMCPHandler";

    public void Execute(UIApplication app)
    {
        Request request;
        lock (_sync)
        {
            request = _pendingRequest
                      ?? throw new InvalidOperationException("No pending RevitMCP request.");
            _pendingRequest = null;
        }

        try
        {
            var doc = app.ActiveUIDocument?.Document
                      ?? throw new InvalidOperationException("No active document open.");

            var action = request.Action;
            var payload = request.Payload;

            request.Result = action switch
            {
                "model/info"            => GetModelInfo(doc),
                "elements/query"        => QueryElements(doc, payload),
                "elements/parameters"   => GetParameters(doc, payload),
                "elements/create-wall"  => CreateWall(doc, payload),
                "elements/place-instance" => PlaceInstance(doc, payload),
                "elements/set-parameter"  => SetParameter(doc, payload),
                "elements/delete"       => DeleteElement(doc, payload),
                "elements/move"         => MoveElement(doc, payload),
                "views/list"            => ListViews(doc, payload),
                "views/activate"        => ActivateView(app, doc, payload),
                "sheets/list"           => ListSheets(doc),
                "sheets/create"         => CreateSheet(doc, payload),
                "sheets/place-view"     => PlaceViewOnSheet(doc, payload),
                "levels/list"           => ListLevels(doc),
                "families/list"         => ListFamilies(doc, payload),
                "rooms/list"            => ListRooms(doc, payload),
                "export/pdf"            => ExportPdf(doc, payload),
                "export/dwg"            => ExportDwg(doc, payload),
                "export/ifc"            => ExportIfc(doc, payload),
                "export/schedule"       => ExportSchedule(doc, payload),
                "commands/run"          => RunCommand(app, payload),
                "commands/run-dynamo"   => RunDynamo(app, payload),
                // ── NEW v2 ──────────────────────────────────────────────
                "takeoff/materials"         => GetMaterialTakeoff(doc, payload),
                "takeoff/export-csv"        => ExportMaterialTakeoffCsv(doc, payload),
                "takeoff/create-schedule"   => CreateMaterialSchedule(doc, payload),
                "clash/detect"              => DetectClashes(doc, payload),
                "clash/export-report"       => ExportClashReport(doc, payload),
                "revisions/issue-drawings"  => IssueDrawings(doc, payload),
                "revisions/history"         => GetRevisionHistory(doc),
                "rooms/finishes"            => GetRoomFinishes(doc, payload),
                "rooms/set-finishes-bulk"   => SetRoomFinishesBulk(doc, payload),
                "views/apply-template-bulk" => ApplyViewTemplateBulk(doc, payload),
                "views/bulk-rename"         => BulkRenameViews(doc, payload),
                "views/create-plans-for-levels" => CreatePlanViewsForLevels(doc, payload),
                "views/duplicate-bulk"      => DuplicateViewsBulk(doc, payload),
                "audit/families"            => AuditFamilies(doc, payload),
                "elements/batch-set-parameters" => BatchSetParameters(doc, payload),
                "elements/import-csv-params"=> ImportParametersFromCsv(doc, payload),
                "sheets/auto-populate"      => AutoPopulateSheets(doc, payload),
                _ => throw new NotSupportedException($"Unknown action: {action}")
            };
        }
        catch (Exception ex)
        {
            request.Error = ex.Message;
        }
        finally
        {
            request.Done.Set();
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static int GetInt(JObject p, string key) =>
        p[key]?.Value<int>() ?? throw new ArgumentException($"Missing required parameter: {key}");

    private static string GetStr(JObject p, string key) =>
        p[key]?.Value<string>() ?? throw new ArgumentException($"Missing required parameter: {key}");

    private static double MmToFt(double mm) => mm / 304.8;

    private static bool TrySetParameterValue(Parameter param, JToken value, out string? error)
    {
        error = null;

        if (param.IsReadOnly)
        {
            error = "read-only";
            return false;
        }

        try
        {
            switch (param.StorageType)
            {
                case StorageType.String:
                    param.Set(value.Type == JTokenType.Null ? "" : value.Value<string>());
                    return true;
                case StorageType.Integer:
                    if (value.Type == JTokenType.Boolean)
                    {
                        param.Set(value.Value<bool>() ? 1 : 0);
                        return true;
                    }
                    param.Set(value.Value<int>());
                    return true;
                case StorageType.Double:
                    if (value.Type == JTokenType.String)
                    {
                        var text = value.Value<string>() ?? "";
                        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                        {
                            error = $"cannot parse double from '{text}'";
                            return false;
                        }
                        param.Set(parsed);
                        return true;
                    }
                    param.Set(value.Value<double>());
                    return true;
                case StorageType.ElementId:
                    param.Set(new ElementId(value.Value<int>()));
                    return true;
                default:
                    error = $"unsupported storage type {param.StorageType}";
                    return false;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static T WithTransaction<T>(Document doc, string name, Func<T> action)
    {
        using var tx = new Transaction(doc, name);
        tx.Start();
        var result = action();
        tx.Commit();
        return result;
    }

    // ─── Read / Query ─────────────────────────────────────────────────────────

    private static JObject GetModelInfo(Document doc)
    {
        var info = doc.ProjectInformation;
        return new JObject
        {
            ["filePath"]     = doc.PathName,
            ["projectName"]  = info.Name,
            ["projectNumber"]= info.Number,
            ["author"]       = info.Author,
            ["status"]       = info.Status,
            ["isWorkshared"] = doc.IsWorkshared,
            ["activeView"]   = doc.ActiveView?.Name,
        };
    }

    private static JObject QueryElements(Document doc, JObject p)
    {
        var category   = p["category"]?.Value<string>();
        var familyName = p["familyName"]?.Value<string>();
        var levelName  = p["levelName"]?.Value<string>();
        var viewName   = p["viewName"]?.Value<string>();
        var limit      = p["limit"]?.Value<int>() ?? 100;

        FilteredElementCollector col;

        if (viewName != null)
        {
            var view = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .FirstOrDefault(v => v.Name == viewName)
                ?? throw new ArgumentException($"View '{viewName}' not found.");
            col = new FilteredElementCollector(doc, view.Id);
        }
        else
        {
            col = new FilteredElementCollector(doc);
        }

        col = col.WhereElementIsNotElementType();

        if (category != null)
        {
            var bic = GetBuiltInCategory(category);
            col = col.OfCategory(bic);
        }

        var elements = col.ToElements()
            .Where(e =>
            {
                if (familyName != null && e is FamilyInstance fi && fi.Symbol.FamilyName != familyName)
                    return false;
                if (levelName != null)
                {
                    var lvlParam = e.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM)
                                 ?? e.get_Parameter(BuiltInParameter.LEVEL_PARAM);
                    if (lvlParam?.AsValueString() != levelName) return false;
                }
                return true;
            })
            .Take(limit)
            .Select(e => new JObject
            {
                ["id"]       = e.Id.Value,
                ["name"]     = e.Name,
                ["category"] = e.Category?.Name,
                ["family"]   = (e is FamilyInstance fi2) ? fi2.Symbol.FamilyName : null,
                ["typeName"] = doc.GetElement(e.GetTypeId())?.Name,
            });

        return new JObject { ["elements"] = new JArray(elements) };
    }

    private static JObject GetParameters(Document doc, JObject p)
    {
        var id = new ElementId(GetInt(p, "elementId"));
        var el = doc.GetElement(id) ?? throw new ArgumentException($"Element {id.Value} not found.");

        var parms = el.Parameters.Cast<Parameter>()
            .Select(param => new JObject
            {
                ["name"]       = param.Definition.Name,
                ["value"]      = param.StorageType switch
                {
                    StorageType.String  => param.AsString(),
                    StorageType.Double  => param.AsDouble(),
                    StorageType.Integer => param.AsInteger(),
                    StorageType.ElementId => param.AsElementId()?.Value,
                    _ => null
                },
                ["valueString"]= param.AsValueString(),
                ["isReadOnly"] = param.IsReadOnly,
                ["storageType"]= param.StorageType.ToString(),
            });

        return new JObject { ["parameters"] = new JArray(parms) };
    }

    private static JObject ListViews(Document doc, JObject p)
    {
        var typeFilter = p["viewType"]?.Value<string>();
        var views = new FilteredElementCollector(doc)
            .OfClass(typeof(View))
            .Cast<View>()
            .Where(v => !v.IsTemplate)
            .Where(v => typeFilter == null || v.ViewType.ToString() == typeFilter)
            .Select(v => new JObject
            {
                ["id"]       = v.Id.Value,
                ["name"]     = v.Name,
                ["viewType"] = v.ViewType.ToString(),
                ["scale"]    = v.Scale,
            });

        return new JObject { ["views"] = new JArray(views) };
    }

    private static JObject ListSheets(Document doc)
    {
        var sheets = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>()
            .Select(s =>
            {
                var viewports = s.GetAllViewports()
                    .Select(vpId => doc.GetElement(vpId) as Viewport)
                    .Where(vp => vp != null)
                    .Select(vp => new JObject
                    {
                        ["viewId"]   = vp!.ViewId.Value,
                        ["viewName"] = doc.GetElement(vp.ViewId)?.Name,
                    });

                return new JObject
                {
                    ["id"]          = s.Id.Value,
                    ["sheetNumber"] = s.SheetNumber,
                    ["name"]        = s.Name,
                    ["views"]       = new JArray(viewports),
                };
            });

        return new JObject { ["sheets"] = new JArray(sheets) };
    }

    private static JObject ListLevels(Document doc)
    {
        var levels = new FilteredElementCollector(doc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .OrderBy(l => l.Elevation)
            .Select(l => new JObject
            {
                ["id"]         = l.Id.Value,
                ["name"]       = l.Name,
                ["elevationFt"]= l.Elevation,
                ["elevationMm"]= l.Elevation * 304.8,
            });

        return new JObject { ["levels"] = new JArray(levels) };
    }

    private static JObject ListFamilies(Document doc, JObject p)
    {
        var category = p["category"]?.Value<string>();
        var families = new FilteredElementCollector(doc)
            .OfClass(typeof(Family))
            .Cast<Family>()
            .Where(f => category == null || f.FamilyCategory?.Name == category)
            .Select(f => new JObject
            {
                ["id"]       = f.Id.Value,
                ["name"]     = f.Name,
                ["category"] = f.FamilyCategory?.Name,
                ["types"]    = new JArray(
                    f.GetFamilySymbolIds()
                     .Select(id => doc.GetElement(id))
                     .Where(e => e != null)
                     .Select(e => new JObject { ["id"] = e.Id.Value, ["name"] = e.Name })
                ),
            });

        return new JObject { ["families"] = new JArray(families) };
    }

    private static JObject ListRooms(Document doc, JObject p)
    {
        var levelName = p["levelName"]?.Value<string>();
        var rooms = new FilteredElementCollector(doc)
            .OfClass(typeof(SpatialElement))
            .OfType<Room>()
            .Where(r => levelName == null || r.Level?.Name == levelName)
            .Select(r => new JObject
            {
                ["id"]      = r.Id.Value,
                ["number"]  = r.Number,
                ["name"]    = r.Name,
                ["level"]   = r.Level?.Name,
                ["areaSqm"] = r.Area * 0.0929,
            });

        return new JObject { ["rooms"] = new JArray(rooms) };
    }

    // ─── Create / Modify ─────────────────────────────────────────────────────

    private static JObject CreateWall(Document doc, JObject p)
    {
        var startX    = p["startX"]?.Value<double>() ?? throw new ArgumentException("startX required");
        var startY    = p["startY"]?.Value<double>() ?? throw new ArgumentException("startY required");
        var endX      = p["endX"]?.Value<double>()   ?? throw new ArgumentException("endX required");
        var endY      = p["endY"]?.Value<double>()   ?? throw new ArgumentException("endY required");
        var levelName = GetStr(p, "levelName");
        var wallTypeName = p["wallTypeName"]?.Value<string>();
        var heightMm  = p["height"]?.Value<double>();

        return WithTransaction(doc, "RevitMCP: Create Wall", () =>
        {
            var level = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .FirstOrDefault(l => l.Name == levelName)
                ?? throw new ArgumentException($"Level '{levelName}' not found.");

            WallType? wallType = null;
            if (wallTypeName != null)
            {
                wallType = new FilteredElementCollector(doc)
                    .OfClass(typeof(WallType))
                    .Cast<WallType>()
                    .FirstOrDefault(wt => wt.Name == wallTypeName)
                    ?? throw new ArgumentException($"Wall type '{wallTypeName}' not found.");
            }

            var line = Line.CreateBound(
                new XYZ(MmToFt(startX), MmToFt(startY), 0),
                new XYZ(MmToFt(endX),   MmToFt(endY),   0)
            );

            var wall = Wall.Create(doc, line, level.Id, false);

            if (wallType != null)
                wall.WallType = wallType;

            if (heightMm.HasValue)
            {
                var heightParam = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM);
                heightParam?.Set(MmToFt(heightMm.Value));
            }

            return new JObject { ["elementId"] = wall.Id.Value, ["success"] = true };
        });
    }

    private static JObject PlaceInstance(Document doc, JObject p)
    {
        var familyName    = GetStr(p, "familyName");
        var typeName      = GetStr(p, "typeName");
        var x             = p["x"]?.Value<double>() ?? 0;
        var y             = p["y"]?.Value<double>() ?? 0;
        var z             = p["z"]?.Value<double>() ?? 0;
        var levelName     = GetStr(p, "levelName");
        var hostId        = p["hostElementId"]?.Value<int?>();
        var rotation      = p["rotation"]?.Value<double?>() ?? 0;

        return WithTransaction(doc, "RevitMCP: Place Instance", () =>
        {
            // Find symbol
            var symbol = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .FirstOrDefault(s => s.FamilyName == familyName && s.Name == typeName)
                ?? throw new ArgumentException($"Family '{familyName}' / type '{typeName}' not found.");

            if (!symbol.IsActive) symbol.Activate();

            var level = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .FirstOrDefault(l => l.Name == levelName)
                ?? throw new ArgumentException($"Level '{levelName}' not found.");

            var point = new XYZ(MmToFt(x), MmToFt(y), MmToFt(z));

            FamilyInstance instance;
            if (hostId.HasValue)
            {
                var host = doc.GetElement(new ElementId(hostId.Value)) as Element
                           ?? throw new ArgumentException($"Host element {hostId} not found.");
                instance = doc.Create.NewFamilyInstance(point, symbol, host, level,
                    Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
            }
            else
            {
                instance = doc.Create.NewFamilyInstance(point, symbol, level,
                    Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
            }

            if (rotation != 0)
            {
                var axis = Line.CreateBound(point, point + XYZ.BasisZ);
                ElementTransformUtils.RotateElement(doc, instance.Id, axis,
                    rotation * Math.PI / 180.0);
            }

            return new JObject { ["elementId"] = instance.Id.Value, ["success"] = true };
        });
    }

    private static JObject SetParameter(Document doc, JObject p)
    {
        var id    = new ElementId(GetInt(p, "elementId"));
        var name  = GetStr(p, "parameterName");
        var value = p["value"] ?? throw new ArgumentException("value required");
        var el    = doc.GetElement(id) ?? throw new ArgumentException($"Element {id.Value} not found.");

        return WithTransaction(doc, "RevitMCP: Set Parameter", () =>
        {
            var param = el.LookupParameter(name)
                        ?? throw new ArgumentException($"Parameter '{name}' not found on element {id.Value}.");

            if (param.IsReadOnly)
                throw new InvalidOperationException($"Parameter '{name}' is read-only.");

            switch (param.StorageType)
            {
                case StorageType.String:  param.Set(value.Value<string>()); break;
                case StorageType.Integer: param.Set(value.Value<int>()); break;
                case StorageType.Double:  param.Set(value.Value<double>()); break;
                case StorageType.ElementId:
                    param.Set(new ElementId(value.Value<int>())); break;
                default:
                    throw new NotSupportedException($"StorageType {param.StorageType} not supported.");
            }

            return new JObject { ["success"] = true };
        });
    }

    private static JObject DeleteElement(Document doc, JObject p)
    {
        var id = new ElementId(GetInt(p, "elementId"));
        return WithTransaction(doc, "RevitMCP: Delete Element", () =>
        {
            doc.Delete(id);
            return new JObject { ["success"] = true, ["deletedId"] = id.Value };
        });
    }

    private static JObject MoveElement(Document doc, JObject p)
    {
        var id     = new ElementId(GetInt(p, "elementId"));
        var dx     = p["deltaX"]?.Value<double>() ?? 0;
        var dy     = p["deltaY"]?.Value<double>() ?? 0;
        var dz     = p["deltaZ"]?.Value<double>() ?? 0;

        return WithTransaction(doc, "RevitMCP: Move Element", () =>
        {
            var el = doc.GetElement(id) ?? throw new ArgumentException($"Element {id.Value} not found.");
            ElementTransformUtils.MoveElement(doc, id, new XYZ(MmToFt(dx), MmToFt(dy), MmToFt(dz)));
            return new JObject { ["success"] = true };
        });
    }

    // ─── Sheets ──────────────────────────────────────────────────────────────

    private static JObject CreateSheet(Document doc, JObject p)
    {
        var number    = GetStr(p, "sheetNumber");
        var name      = GetStr(p, "sheetName");
        var tbName    = p["titleBlockName"]?.Value<string>();

        return WithTransaction(doc, "RevitMCP: Create Sheet", () =>
        {
            FamilySymbol? tb = null;
            if (tbName != null)
            {
                tb = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .OfClass(typeof(FamilySymbol))
                    .Cast<FamilySymbol>()
                    .FirstOrDefault(s => s.Name == tbName)
                    ?? throw new ArgumentException($"Title block '{tbName}' not found.");
            }

            var sheet = tb != null
                ? ViewSheet.Create(doc, tb.Id)
                : ViewSheet.Create(doc, ElementId.InvalidElementId);

            sheet.SheetNumber = number;
            sheet.Name = name;

            return new JObject { ["sheetId"] = sheet.Id.Value, ["success"] = true };
        });
    }

    private static JObject PlaceViewOnSheet(Document doc, JObject p)
    {
        var sheetId = new ElementId(GetInt(p, "sheetId"));
        var viewId  = new ElementId(GetInt(p, "viewId"));
        var cx      = p["centerX"]?.Value<double>() ?? 0;
        var cy      = p["centerY"]?.Value<double>() ?? 0;

        return WithTransaction(doc, "RevitMCP: Place View on Sheet", () =>
        {
            var sheet = doc.GetElement(sheetId) as ViewSheet
                        ?? throw new ArgumentException("Sheet not found.");
            var view  = doc.GetElement(viewId)  as View
                        ?? throw new ArgumentException("View not found.");

            var vp = Viewport.Create(doc, sheet.Id, view.Id,
                new XYZ(MmToFt(cx), MmToFt(cy), 0));

            return new JObject { ["viewportId"] = vp.Id.Value, ["success"] = true };
        });
    }

    private static JObject ActivateView(UIApplication app, Document doc, JObject p)
    {
        View? view = null;
        var viewId   = p["viewId"]?.Value<int?>();
        var viewName = p["viewName"]?.Value<string>();

        if (viewId.HasValue)
            view = doc.GetElement(new ElementId(viewId.Value)) as View;
        else if (viewName != null)
            view = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                       .FirstOrDefault(v => v.Name == viewName);

        if (view == null) throw new ArgumentException("View not found.");

        app.ActiveUIDocument.ActiveView = view;
        return new JObject { ["success"] = true, ["activatedView"] = view.Name };
    }

    // ─── Export ──────────────────────────────────────────────────────────────

    private static JObject ExportPdf(Document doc, JObject p)
    {
        var outputPath  = GetStr(p, "outputPath");
        var sheetIds    = p["sheetIds"]?.ToObject<int[]>();
        var viewIds     = p["viewIds"]?.ToObject<int[]>();
        var combine     = p["combineIntoOne"]?.Value<bool>() ?? false;
        var paperSize   = p["paperSize"]?.Value<string>() ?? "A1";

        Directory.CreateDirectory(outputPath);

        var opts = new PDFExportOptions
        {
            Combine = combine,
            PaperFormat = paperSize switch
            {
                "A0" => ExportPaperFormat.ISO_A0,
                "A2" => ExportPaperFormat.ISO_A2,
                "A3" => ExportPaperFormat.ISO_A3,
                "A4" => ExportPaperFormat.ISO_A4,
                _    => ExportPaperFormat.ISO_A1,
            },
        };

        var ids = new List<ElementId>();
        if (sheetIds != null) ids.AddRange(sheetIds.Select(i => new ElementId(i)));
        if (viewIds  != null) ids.AddRange(viewIds.Select(i => new ElementId(i)));

        if (ids.Count == 0)
        {
            // Export all sheets
            ids.AddRange(new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Select(e => e.Id));
        }

        doc.Export(outputPath, ids, opts);
        return new JObject { ["success"] = true, ["outputPath"] = outputPath };
    }

    private static JObject ExportDwg(Document doc, JObject p)
    {
        var outputPath = GetStr(p, "outputPath");
        var viewIds    = p["viewIds"]?.ToObject<int[]>();
        var format     = p["format"]?.Value<string>() ?? "DWG";

        Directory.CreateDirectory(outputPath);

        var opts = new DWGExportOptions
        {
            FileVersion = ACADVersion.R2013,
        };

        var ids = viewIds != null
            ? viewIds.Select(i => new ElementId(i)).ToList()
            : new List<ElementId> { doc.ActiveView.Id };

        doc.Export(outputPath, outputPath, ids, opts);
        return new JObject { ["success"] = true, ["outputPath"] = outputPath };
    }

    private static JObject ExportIfc(Document doc, JObject p)
    {
        var outputPath = GetStr(p, "outputPath");
        var ifcVersion = p["ifcVersion"]?.Value<string>() ?? "IFC4";
        var baseQty    = p["exportBaseQuantities"]?.Value<bool>() ?? true;

        var opts = new IFCExportOptions
        {
            FileVersion = ifcVersion switch
            {
                "IFC2x3" => IFCVersion.IFC2x3CV2,
                "IFC4x3" => IFCVersion.IFCSG,
                _        => IFCVersion.IFC4,
            },
            ExportBaseQuantities = baseQty,
        };

        using var tx = new Transaction(doc, "RevitMCP: Export IFC");
        tx.Start();
        doc.Export(Path.GetDirectoryName(outputPath)!, Path.GetFileNameWithoutExtension(outputPath), opts);
        tx.Commit();

        return new JObject { ["success"] = true, ["outputPath"] = outputPath };
    }

    private static JObject ExportSchedule(Document doc, JObject p)
    {
        var schedId   = new ElementId(GetInt(p, "scheduleViewId"));
        var outPath   = GetStr(p, "outputPath");
        var delimiter = p["delimiter"]?.Value<string>() ?? "comma";

        var schedule = doc.GetElement(schedId) as ViewSchedule
                       ?? throw new ArgumentException("Schedule view not found.");

        var opts = new ViewScheduleExportOptions
        {
            FieldDelimiter = delimiter switch
            {
                "tab"       => "\t",
                "semicolon" => ";",
                _           => ",",
            },
            Title = false,
        };

        schedule.Export(Path.GetDirectoryName(outPath)!, Path.GetFileName(outPath), opts);
        return new JObject { ["success"] = true, ["outputPath"] = outPath };
    }

    // ─── Commands ────────────────────────────────────────────────────────────

    private static JObject RunCommand(UIApplication app, JObject p)
    {
        var commandId = GetStr(p, "commandId");
        if (!Enum.TryParse<PostableCommand>(commandId, out var cmd))
            throw new ArgumentException($"Unknown PostableCommand: {commandId}");

        var revitCommandId = RevitCommandId.LookupPostableCommandId(cmd);
        app.PostCommand(revitCommandId);
        return new JObject { ["success"] = true, ["command"] = commandId };
    }

    private static JObject RunDynamo(UIApplication app, JObject p)
    {
        var scriptPath   = GetStr(p, "scriptPath");
        var journalData  = p["journalData"]?.ToObject<Dictionary<string, string>>() ?? new();

        if (!File.Exists(scriptPath))
            throw new FileNotFoundException($"Dynamo script not found: {scriptPath}");

        // Dynamo must be triggered via journal. We build a minimal journal key dict.
        journalData["dynamo_script_path"] = scriptPath;

        // The actual Dynamo automation API requires DynamoRevit references which are
        // version-dependent. Here we use the standard journal approach.
        var cmdId = RevitCommandId.LookupCommandId("ID_VISUAL_PROGRAMMING_DYNAMO");
        app.PostCommand(cmdId);

        return new JObject
        {
            ["success"]    = true,
            ["scriptPath"] = scriptPath,
            ["note"]       = "Dynamo command posted. Script must be configured to auto-run in Dynamo settings."
        };
    }

    // ─── Category helper ─────────────────────────────────────────────────────

    private static BuiltInCategory GetBuiltInCategory(string name) => name switch
    {
        "Walls"      => BuiltInCategory.OST_Walls,
        "Doors"      => BuiltInCategory.OST_Doors,
        "Windows"    => BuiltInCategory.OST_Windows,
        "Floors"     => BuiltInCategory.OST_Floors,
        "Roofs"      => BuiltInCategory.OST_Roofs,
        "Ceilings"   => BuiltInCategory.OST_Ceilings,
        "Rooms"      => BuiltInCategory.OST_Rooms,
        "Columns"    => BuiltInCategory.OST_Columns,
        "Beams"      => BuiltInCategory.OST_StructuralFraming,
        "Furniture"  => BuiltInCategory.OST_Furniture,
        "Stairs"     => BuiltInCategory.OST_Stairs,
        "Railings"   => BuiltInCategory.OST_StairsRailing,
        "Grids"      => BuiltInCategory.OST_Grids,
        "Levels"     => BuiltInCategory.OST_Levels,
        "Sheets"     => BuiltInCategory.OST_Sheets,
        "MEP"        => BuiltInCategory.OST_MechanicalEquipment,
        _            => throw new ArgumentException($"Unknown category: '{name}'. Use standard Revit category names.")
    };

    // ════════════════════════════════════════════════════════════════════════
    // V2 — MATERIAL TAKEOFF
    // ════════════════════════════════════════════════════════════════════════

    private static readonly string[] DefaultTakeoffCategories =
        { "Walls", "Floors", "Roofs", "Ceilings", "Columns", "Beams" };

    private static JObject GetMaterialTakeoff(Document doc, JObject p)
    {
        var cats     = p["categories"]?.ToObject<string[]>() ?? DefaultTakeoffCategories;
        var level    = p["levelName"]?.Value<string>();
        var layers   = p["includeLayerBreakdown"]?.Value<bool>() ?? true;

        var rows = CollectMaterialRows(doc, cats, level, layers);
        return new JObject { ["materials"] = new JArray(rows) };
    }

    private static JObject ExportMaterialTakeoffCsv(Document doc, JObject p)
    {
        var outPath  = GetStr(p, "outputPath");
        var cats     = p["categories"]?.ToObject<string[]>() ?? DefaultTakeoffCategories;
        var level    = p["levelName"]?.Value<string>();
        var layers   = p["includeLayerBreakdown"]?.Value<bool>() ?? true;

        var rows = CollectMaterialRows(doc, cats, level, layers);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

        using var w = new StreamWriter(outPath, false, System.Text.Encoding.UTF8);
        w.WriteLine("Material,Category,TotalAreaM2,TotalVolM3,ElementCount");
        foreach (var row in rows)
            w.WriteLine($"\"{row["material"]}\",\"{row["category"]}\",{row["totalAreaM2"]:F3},{row["totalVolM3"]:F4},{row["elementCount"]}");

        return new JObject { ["success"] = true, ["outputPath"] = outPath, ["rowCount"] = rows.Count };
    }

    private static List<JObject> CollectMaterialRows(Document doc, string[] cats, string? levelFilter, bool layers)
    {
        // key = (materialName, category)
        var agg = new Dictionary<(string, string), (double area, double vol, int count)>();

        foreach (var catName in cats)
        {
            BuiltInCategory bic;
            try { bic = GetBuiltInCategory(catName); } catch { continue; }

            var elements = new FilteredElementCollector(doc)
                .OfCategory(bic)
                .WhereElementIsNotElementType()
                .ToElements()
                .Where(e =>
                {
                    if (levelFilter == null) return true;
                    var lp = e.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM)
                           ?? e.get_Parameter(BuiltInParameter.LEVEL_PARAM);
                    return lp?.AsValueString() == levelFilter;
                });

            foreach (var el in elements)
            {
                if (layers && el is Wall wall && wall.WallType.Kind == WallKind.Basic)
                {
                    // Compound wall — iterate layers
                    var cs = wall.WallType.GetCompoundStructure();
                    if (cs != null)
                    {
                        var wallArea = wall.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED)?.AsDouble() ?? 0;
                        foreach (var layer in cs.GetLayers())
                        {
                            var matEl = doc.GetElement(layer.MaterialId);
                            var matName = matEl?.Name ?? "No Material";
                            var layerVol = wallArea * layer.Width; // ft³
                            var key = (matName, catName);
                            var cur = agg.GetValueOrDefault(key);
                            agg[key] = (cur.area + wallArea * 0.0929, cur.vol + layerVol * 0.0283168, cur.count + 1);
                        }
                        continue;
                    }
                }

                // Generic: get primary material
                var matParam = el.get_Parameter(BuiltInParameter.STRUCTURAL_MATERIAL_PARAM)
                             ?? el.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
                var materialName = "No Material";
                if (matParam != null)
                {
                    var matId = matParam.AsElementId();
                    materialName = doc.GetElement(matId)?.Name ?? "No Material";
                }

                var area = el.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED)?.AsDouble() ?? 0;
                var vol  = el.get_Parameter(BuiltInParameter.HOST_VOLUME_COMPUTED)?.AsDouble() ?? 0;
                var k2   = (materialName, catName);
                var c2   = agg.GetValueOrDefault(k2);
                agg[k2] = (c2.area + area * 0.0929, c2.vol + vol * 0.0283168, c2.count + 1);
            }
        }

        return agg.Select(kv => new JObject
        {
            ["material"]    = kv.Key.Item1,
            ["category"]    = kv.Key.Item2,
            ["totalAreaM2"] = Math.Round(kv.Value.area, 3),
            ["totalVolM3"]  = Math.Round(kv.Value.vol, 4),
            ["elementCount"]= kv.Value.count,
        }).OrderBy(r => r["category"]!.ToString()).ThenBy(r => r["material"]!.ToString())
          .ToList();
    }

    private static JObject CreateMaterialSchedule(Document doc, JObject p)
    {
        var catName      = GetStr(p, "category");
        var scheduleName = p["scheduleName"]?.Value<string>() ?? $"{catName} Material Takeoff";
        var bic          = GetBuiltInCategory(catName);

        return WithTransaction(doc, "RevitMCP: Create Material Schedule", () =>
        {
            var catId    = new ElementId(bic);
            var schedule = ViewSchedule.CreateMaterialTakeoff(doc, catId);
            schedule.Name = scheduleName;

            // Add standard fields
            var def = schedule.Definition;
            var fields = new[] { "Material: Name", "Material: Area", "Material: Volume", "Count" };
            foreach (var fieldName in fields)
            {
                var sf = def.GetSchedulableFields()
                            .FirstOrDefault(f => f.GetName(doc) == fieldName);
                if (sf != null) def.AddField(sf);
            }

            return new JObject { ["scheduleId"] = schedule.Id.Value, ["name"] = schedule.Name, ["success"] = true };
        });
    }

    // ════════════════════════════════════════════════════════════════════════
    // V2 — CLASH DETECTION
    // ════════════════════════════════════════════════════════════════════════

    private static JObject DetectClashes(Document doc, JObject p)
    {
        var catA      = GetStr(p, "categoryA");
        var catB      = GetStr(p, "categoryB");
        var level     = p["levelName"]?.Value<string>();
        var tolerance = p["tolerance"]?.Value<double>() ?? 0; // mm
        var tolFt     = MmToFt(tolerance);

        var clashes = FindClashes(doc, catA, catB, level, tolFt);
        return new JObject
        {
            ["clashCount"] = clashes.Count,
            ["clashes"]    = new JArray(clashes),
        };
    }

    private static JObject ExportClashReport(Document doc, JObject p)
    {
        var outPath   = GetStr(p, "outputPath");
        var catA      = GetStr(p, "categoryA");
        var catB      = GetStr(p, "categoryB");
        var format    = p["format"]?.Value<string>() ?? "html";
        var tolerance = p["tolerance"]?.Value<double>() ?? 0;
        var tolFt     = MmToFt(tolerance);

        var clashes = FindClashes(doc, catA, catB, null, tolFt);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

        if (format == "csv")
        {
            using var w = new StreamWriter(outPath, false, System.Text.Encoding.UTF8);
            w.WriteLine("ClashId,ElementA_Id,ElementA_Category,ElementA_Type,ElementB_Id,ElementB_Category,ElementB_Type,LocationX_mm,LocationY_mm,LocationZ_mm");
            int i = 1;
            foreach (var c in clashes)
                w.WriteLine($"{i++},{c["elementA_Id"]},{c["elementA_Category"]},{c["elementA_Type"]},{c["elementB_Id"]},{c["elementB_Category"]},{c["elementB_Type"]},{c["locationX_mm"]},{c["locationY_mm"]},{c["locationZ_mm"]}");
        }
        else
        {
            var rows = string.Join("\n", clashes.Select((c, i) =>
                $"<tr><td>{i+1}</td><td>{c["elementA_Id"]}</td><td>{c["elementA_Category"]}</td><td>{c["elementA_Type"]}</td>" +
                $"<td>{c["elementB_Id"]}</td><td>{c["elementB_Category"]}</td><td>{c["elementB_Type"]}</td>" +
                $"<td>{c["locationX_mm"]}, {c["locationY_mm"]}, {c["locationZ_mm"]}</td></tr>"));

            var html = $@"<!DOCTYPE html><html><head><meta charset='utf-8'><title>Clash Report</title>
<style>body{{font-family:Arial;font-size:12px}} table{{border-collapse:collapse;width:100%}}
th,td{{border:1px solid #ccc;padding:6px 8px}} th{{background:#2c3e50;color:#fff}} tr:nth-child(even){{background:#f5f5f5}}</style></head>
<body><h2>Clash Detection Report</h2>
<p>Categories: <b>{catA}</b> vs <b>{catB}</b> &nbsp;|&nbsp; Clashes found: <b>{clashes.Count}</b> &nbsp;|&nbsp; Generated: {DateTime.Now:yyyy-MM-dd HH:mm}</p>
<table><thead><tr><th>#</th><th>ID A</th><th>Cat A</th><th>Type A</th><th>ID B</th><th>Cat B</th><th>Type B</th><th>Location (mm)</th></tr></thead>
<tbody>{rows}</tbody></table></body></html>";
            File.WriteAllText(outPath, html, System.Text.Encoding.UTF8);
        }

        return new JObject { ["success"] = true, ["clashCount"] = clashes.Count, ["outputPath"] = outPath };
    }

    private static List<JObject> FindClashes(Document doc, string catA, string catB, string? levelFilter, double tolFt)
    {
        var elementsA = new FilteredElementCollector(doc)
            .OfCategory(GetBuiltInCategory(catA))
            .WhereElementIsNotElementType()
            .ToElements()
            .Where(e => levelFilter == null || (e.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM)?.AsValueString() == levelFilter))
            .ToList();

        var elementsB = new FilteredElementCollector(doc)
            .OfCategory(GetBuiltInCategory(catB))
            .WhereElementIsNotElementType()
            .ToElements()
            .Where(e => levelFilter == null || (e.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM)?.AsValueString() == levelFilter))
            .ToList();

        var clashes = new List<JObject>();

        foreach (var a in elementsA)
        {
            var solidA = GetSolid(a);
            if (solidA == null) continue;

            foreach (var b in elementsB)
            {
                if (a.Id == b.Id) continue;
                var solidB = GetSolid(b);
                if (solidB == null) continue;

                try
                {
                    var intersection = BooleanOperationsUtils.ExecuteBooleanOperation(
                        solidA, solidB, BooleanOperationsType.Intersect);
                    if (intersection != null && intersection.Volume > tolFt * tolFt * tolFt)
                    {
                        var bb = intersection.GetBoundingBox();
                        var center = (bb.Min + bb.Max) / 2;
                        clashes.Add(new JObject
                        {
                            ["elementA_Id"]       = a.Id.Value,
                            ["elementA_Category"] = catA,
                            ["elementA_Type"]     = doc.GetElement(a.GetTypeId())?.Name,
                            ["elementB_Id"]       = b.Id.Value,
                            ["elementB_Category"] = catB,
                            ["elementB_Type"]     = doc.GetElement(b.GetTypeId())?.Name,
                            ["locationX_mm"]      = Math.Round(center.X * 304.8),
                            ["locationY_mm"]      = Math.Round(center.Y * 304.8),
                            ["locationZ_mm"]      = Math.Round(center.Z * 304.8),
                            ["volumeMM3"]         = Math.Round(intersection.Volume * 304.8 * 304.8 * 304.8),
                        });
                    }
                }
                catch { /* Boolean op failure on complex geometry — skip */ }
            }
        }
        return clashes;
    }

    private static Solid? GetSolid(Element el)
    {
        var opts = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Coarse };
        var geom = el.get_Geometry(opts);
        if (geom == null) return null;
        foreach (var obj in geom)
        {
            if (obj is Solid s && s.Volume > 0) return s;
            if (obj is GeometryInstance gi)
                foreach (var inner in gi.GetInstanceGeometry())
                    if (inner is Solid si && si.Volume > 0) return si;
        }
        return null;
    }

    // ════════════════════════════════════════════════════════════════════════
    // V2 — REVISION / DRAWING ISSUE AUTOMATION
    // ════════════════════════════════════════════════════════════════════════

    private static JObject IssueDrawings(Document doc, JObject p)
    {
        var description  = GetStr(p, "revisionDescription");
        var date         = GetStr(p, "revisionDate");
        var sheetIds     = p["sheetIds"]?.ToObject<int[]>();
        var exportPath   = p["exportPdfPath"]?.Value<string>();

        return WithTransaction(doc, "RevitMCP: Issue Drawings", () =>
        {
            // Create revision
            var rev = Revision.Create(doc);
            rev.Description = description;
            rev.RevisionDate = date;

            // Stamp sheets
            var sheets = sheetIds != null
                ? sheetIds.Select(id => doc.GetElement(new ElementId(id)) as ViewSheet).Where(s => s != null)
                : new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>();

            var stamped = 0;
            foreach (var sheet in sheets)
            {
                if (sheet == null) continue;
                var revIds = sheet.GetAdditionalRevisionIds().ToList();
                revIds.Add(rev.Id);
                sheet.SetAdditionalRevisionIds(revIds);
                stamped++;
            }

            var result = new JObject
            {
                ["success"]     = true,
                ["revisionId"]  = rev.Id.Value,
                ["sheetsStamped"] = stamped,
            };

            if (exportPath != null)
            {
                Directory.CreateDirectory(exportPath);
                var exportIds = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheet))
                    .Select(e => e.Id)
                    .ToList();
                doc.Export(exportPath, exportIds, new PDFExportOptions { Combine = true });
                result["pdfExportPath"] = exportPath;
            }

            return result;
        });
    }

    private static JObject GetRevisionHistory(Document doc)
    {
        var revisions = Revision.GetAllRevisionIds(doc)
            .Select(id => doc.GetElement(id) as Revision)
            .Where(r => r != null)
            .Select(r =>
            {
                var sheetCount = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheet))
                    .Cast<ViewSheet>()
                    .Count(s => s.GetAllRevisionIds().Contains(r!.Id));

                return new JObject
                {
                    ["id"]          = r!.Id.Value,
                    ["sequence"]    = r.SequenceNumber,
                    ["description"] = r.Description,
                    ["date"]        = r.RevisionDate,
                    ["sheetCount"]  = sheetCount,
                };
            });

        return new JObject { ["revisions"] = new JArray(revisions) };
    }

    // ════════════════════════════════════════════════════════════════════════
    // V2 — ROOM FINISHES
    // ════════════════════════════════════════════════════════════════════════

    private static readonly string[] FinishParams =
        { "Floor Finish", "Wall Finish", "Ceiling Finish", "Base Finish", "Comments" };

    private static JObject GetRoomFinishes(Document doc, JObject p)
    {
        var levelName = p["levelName"]?.Value<string>();
        var rooms = new FilteredElementCollector(doc)
            .OfClass(typeof(SpatialElement))
            .OfType<Room>()
            .Where(r => levelName == null || r.Level?.Name == levelName)
            .Select(r =>
            {
                var obj = new JObject
                {
                    ["id"]     = r.Id.Value,
                    ["number"] = r.Number,
                    ["name"]   = r.Name,
                    ["level"]  = r.Level?.Name,
                };
                foreach (var fp in FinishParams)
                {
                    var param = r.LookupParameter(fp);
                    obj[fp.Replace(" ", "")] = param?.AsString() ?? "";
                }
                return obj;
            });

        return new JObject { ["rooms"] = new JArray(rooms) };
    }

    private static JObject SetRoomFinishesBulk(Document doc, JObject p)
    {
        var updates = p["updates"]?.ToObject<JArray>()
                      ?? throw new ArgumentException("updates array required");

        return WithTransaction(doc, "RevitMCP: Set Room Finishes Bulk", () =>
        {
            int updated = 0;
            var errors  = new JArray();

            foreach (var upd in updates.Cast<JObject>())
            {
                var roomId = upd["roomId"]?.Value<int>() ?? -1;
                var room   = doc.GetElement(new ElementId(roomId)) as Room;
                if (room == null) { errors.Add($"Room {roomId} not found"); continue; }

                var map = new Dictionary<string, string?>
                {
                    { "Floor Finish",   upd["floorFinish"]?.Value<string>() },
                    { "Wall Finish",    upd["wallFinish"]?.Value<string>() },
                    { "Ceiling Finish", upd["ceilingFinish"]?.Value<string>() },
                    { "Base Finish",    upd["baseFinish"]?.Value<string>() },
                    { "Comments",       upd["comments"]?.Value<string>() },
                };

                foreach (var (paramName, val) in map)
                {
                    if (val == null) continue;
                    room.LookupParameter(paramName)?.Set(val);
                }
                updated++;
            }

            return new JObject { ["success"] = true, ["roomsUpdated"] = updated, ["errors"] = errors };
        });
    }

    // ════════════════════════════════════════════════════════════════════════
    // V2 — VIEW TEMPLATE & BULK RENAME
    // ════════════════════════════════════════════════════════════════════════

    private static JObject ApplyViewTemplateBulk(Document doc, JObject p)
    {
        var templateName = GetStr(p, "templateName");
        var viewIds      = p["viewIds"]?.ToObject<int[]>();
        var viewType     = p["viewType"]?.Value<string>();
        var nameContains = p["nameContains"]?.Value<string>();

        // Find template
        var template = new FilteredElementCollector(doc)
            .OfClass(typeof(View))
            .Cast<View>()
            .FirstOrDefault(v => v.IsTemplate && v.Name == templateName)
            ?? throw new ArgumentException($"View template '{templateName}' not found.");

        return WithTransaction(doc, "RevitMCP: Apply View Template Bulk", () =>
        {
            IEnumerable<View> targets;
            if (viewIds != null)
            {
                targets = viewIds.Select(id => doc.GetElement(new ElementId(id)) as View).Where(v => v != null)!;
            }
            else
            {
                targets = new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Where(v => !v.IsTemplate)
                    .Where(v => viewType == null || v.ViewType.ToString() == viewType)
                    .Where(v => nameContains == null || v.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase));
            }

            int count = 0;
            foreach (var view in targets)
            {
                view.ViewTemplateId = template.Id;
                count++;
            }

            return new JObject { ["success"] = true, ["viewsUpdated"] = count, ["template"] = templateName };
        });
    }

    private static JObject BulkRenameViews(Document doc, JObject p)
    {
        var targets      = p["targets"]?.Value<string>() ?? "views";
        var findText     = p["findText"]?.Value<string>();
        var replaceText  = p["replaceText"]?.Value<string>() ?? "";
        var addPrefix    = p["addPrefix"]?.Value<string>() ?? "";
        var addSuffix    = p["addSuffix"]?.Value<string>() ?? "";
        var nameContains = p["nameContains"]?.Value<string>();
        var dryRun       = p["dryRun"]?.Value<bool>() ?? false;

        IEnumerable<Element> items = targets switch
        {
            "sheets" => new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).ToElements(),
            "both"   => new FilteredElementCollector(doc).OfClass(typeof(View)).ToElements(),
            _        => new FilteredElementCollector(doc).OfClass(typeof(View))
                            .Cast<View>().Where(v => !v.IsTemplate).Cast<Element>()
        };

        if (nameContains != null)
            items = items.Where(e => e.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase));

        var plan = items.Select(e =>
        {
            var oldName = e.Name;
            var newName = oldName;
            if (findText != null) newName = newName.Replace(findText, replaceText);
            if (!string.IsNullOrEmpty(addPrefix)) newName = addPrefix + newName;
            if (!string.IsNullOrEmpty(addSuffix)) newName = newName + addSuffix;
            return (el: e, oldName, newName);
        }).Where(x => x.oldName != x.newName).ToList();

        if (dryRun)
        {
            var preview = plan.Select(x => new JObject { ["id"] = x.el.Id.Value, ["from"] = x.oldName, ["to"] = x.newName });
            return new JObject { ["dryRun"] = true, ["changes"] = new JArray(preview), ["count"] = plan.Count };
        }

        return WithTransaction(doc, "RevitMCP: Bulk Rename", () =>
        {
            foreach (var (el, _, newName) in plan)
            {
                try { el.Name = newName; } catch { /* some views disallow rename */ }
            }
            return new JObject { ["success"] = true, ["renamed"] = plan.Count };
        });
    }

    // ════════════════════════════════════════════════════════════════════════
    // V2 — FAMILY AUDIT
    // ════════════════════════════════════════════════════════════════════════

    private static string UniqueViewName(Document doc, string desiredName)
    {
        var existing = new FilteredElementCollector(doc)
            .OfClass(typeof(View))
            .Cast<View>()
            .Select(v => v.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!existing.Contains(desiredName)) return desiredName;

        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{desiredName} ({i})";
            if (!existing.Contains(candidate)) return candidate;
        }

        throw new InvalidOperationException($"Could not create a unique view name for '{desiredName}'.");
    }

    private static JObject CreatePlanViewsForLevels(Document doc, JObject p)
    {
        var levelNames = p["levelNames"]?.ToObject<string[]>();
        var viewFamilyText = p["viewFamily"]?.Value<string>() ?? "FloorPlan";
        var nameTemplate = p["nameTemplate"]?.Value<string>() ?? "{levelName} - {viewFamily}";
        var templateName = p["viewTemplateName"]?.Value<string>();
        var skipExisting = p["skipExisting"]?.Value<bool>() ?? true;
        var dryRun = p["dryRun"]?.Value<bool>() ?? true;

        var viewFamily = viewFamilyText.ToLowerInvariant() switch
        {
            "ceilingplan" or "ceiling" => ViewFamily.CeilingPlan,
            "structuralplan" or "structural" => ViewFamily.StructuralPlan,
            _ => ViewFamily.FloorPlan
        };

        var viewFamilyType = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewFamilyType))
            .Cast<ViewFamilyType>()
            .FirstOrDefault(vft => vft.ViewFamily == viewFamily)
            ?? throw new ArgumentException($"No ViewFamilyType found for {viewFamily}.");

        var template = templateName == null
            ? null
            : new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .FirstOrDefault(v => v.IsTemplate && v.Name == templateName)
                ?? throw new ArgumentException($"View template '{templateName}' not found.");

        var levels = new FilteredElementCollector(doc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .Where(l => levelNames == null || levelNames.Contains(l.Name))
            .OrderBy(l => l.Elevation)
            .ToList();

        var existingNames = new FilteredElementCollector(doc)
            .OfClass(typeof(View))
            .Cast<View>()
            .Select(v => v.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var plan = levels.Select(level =>
        {
            var name = nameTemplate
                .Replace("{levelName}", level.Name)
                .Replace("{viewFamily}", viewFamily.ToString());
            return new JObject
            {
                ["levelId"] = level.Id.Value,
                ["levelName"] = level.Name,
                ["viewName"] = name,
                ["exists"] = existingNames.Contains(name),
            };
        }).ToList();

        if (dryRun)
            return new JObject { ["dryRun"] = true, ["planned"] = new JArray(plan), ["count"] = plan.Count };

        return WithTransaction(doc, "RevitMCP: Create Plan Views For Levels", () =>
        {
            var created = new JArray();

            foreach (var item in plan)
            {
                if (skipExisting && item["exists"]!.Value<bool>())
                {
                    item["skipped"] = true;
                    item["reason"] = "view name already exists";
                    created.Add(item);
                    continue;
                }

                var view = ViewPlan.Create(doc, viewFamilyType.Id, new ElementId(item["levelId"]!.Value<int>()));
                view.Name = UniqueViewName(doc, item["viewName"]!.Value<string>()!);
                if (template != null) view.ViewTemplateId = template.Id;

                item["viewId"] = view.Id.Value;
                item["created"] = true;
                created.Add(item);
            }

            return new JObject
            {
                ["success"] = true,
                ["viewsCreated"] = created.Count(v => v["created"]?.Value<bool>() == true),
                ["details"] = created,
            };
        });
    }

    private static JObject DuplicateViewsBulk(Document doc, JObject p)
    {
        var viewIds = p["viewIds"]?.ToObject<int[]>();
        var viewType = p["viewType"]?.Value<string>();
        var nameContains = p["nameContains"]?.Value<string>();
        var nameTemplate = p["nameTemplate"]?.Value<string>() ?? "{viewName} - Copy";
        var templateName = p["viewTemplateName"]?.Value<string>();
        var dryRun = p["dryRun"]?.Value<bool>() ?? true;
        var optionText = p["duplicateOption"]?.Value<string>() ?? "WithDetailing";

        var duplicateOption = optionText.ToLowerInvariant() switch
        {
            "asdependent" or "dependent" => ViewDuplicateOption.AsDependent,
            "duplicate" => ViewDuplicateOption.Duplicate,
            _ => ViewDuplicateOption.WithDetailing
        };

        var template = templateName == null
            ? null
            : new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .FirstOrDefault(v => v.IsTemplate && v.Name == templateName)
                ?? throw new ArgumentException($"View template '{templateName}' not found.");

        IEnumerable<View> query = viewIds != null
            ? viewIds.Select(id => doc.GetElement(new ElementId(id)) as View).OfType<View>()
            : new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate && v.CanBePrinted);

        query = query
            .Where(v => viewType == null || v.ViewType.ToString() == viewType)
            .Where(v => nameContains == null || v.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
            .Where(v => v.CanViewBeDuplicated(duplicateOption));

        var plan = query.Select((view, index) =>
        {
            var newName = nameTemplate
                .Replace("{viewName}", view.Name)
                .Replace("{index}", (index + 1).ToString("D2", CultureInfo.InvariantCulture));
            return new JObject
            {
                ["sourceViewId"] = view.Id.Value,
                ["sourceViewName"] = view.Name,
                ["newViewName"] = newName,
            };
        }).ToList();

        if (dryRun)
            return new JObject { ["dryRun"] = true, ["planned"] = new JArray(plan), ["count"] = plan.Count };

        return WithTransaction(doc, "RevitMCP: Duplicate Views Bulk", () =>
        {
            var duplicated = new JArray();

            foreach (var item in plan)
            {
                var source = doc.GetElement(new ElementId(item["sourceViewId"]!.Value<int>())) as View;
                if (source == null) continue;

                var newId = source.Duplicate(duplicateOption);
                var newView = doc.GetElement(newId) as View;
                if (newView == null) continue;

                newView.Name = UniqueViewName(doc, item["newViewName"]!.Value<string>()!);
                if (template != null) newView.ViewTemplateId = template.Id;

                item["newViewId"] = newView.Id.Value;
                item["created"] = true;
                duplicated.Add(item);
            }

            return new JObject { ["success"] = true, ["viewsDuplicated"] = duplicated.Count, ["details"] = duplicated };
        });
    }

    private static JObject BatchSetParameters(Document doc, JObject p)
    {
        var values = p["parameterValues"] as JObject
                     ?? throw new ArgumentException("parameterValues object required");
        var category = p["category"]?.Value<string>();
        var familyName = p["familyName"]?.Value<string>();
        var levelName = p["levelName"]?.Value<string>();
        var viewName = p["viewName"]?.Value<string>();
        var nameContains = p["nameContains"]?.Value<string>();
        var dryRun = p["dryRun"]?.Value<bool>() ?? true;
        var limit = p["limit"]?.Value<int>() ?? 1000;

        FilteredElementCollector col;
        if (viewName != null)
        {
            var view = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .FirstOrDefault(v => v.Name == viewName)
                ?? throw new ArgumentException($"View '{viewName}' not found.");
            col = new FilteredElementCollector(doc, view.Id);
        }
        else
        {
            col = new FilteredElementCollector(doc);
        }

        col = col.WhereElementIsNotElementType();
        if (category != null) col = col.OfCategory(GetBuiltInCategory(category));

        var elements = col.ToElements()
            .Where(e => familyName == null || e is FamilyInstance fi && fi.Symbol.FamilyName == familyName)
            .Where(e => nameContains == null || e.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
            .Where(e =>
            {
                if (levelName == null) return true;
                var lvlParam = e.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM)
                             ?? e.get_Parameter(BuiltInParameter.LEVEL_PARAM);
                return lvlParam?.AsValueString() == levelName;
            })
            .Take(limit)
            .ToList();

        var preview = new JArray(elements.Select(e => new JObject
        {
            ["id"] = e.Id.Value,
            ["name"] = e.Name,
            ["category"] = e.Category?.Name,
        }));

        if (dryRun)
            return new JObject { ["dryRun"] = true, ["matchedElements"] = elements.Count, ["parameterValues"] = values, ["preview"] = preview };

        return WithTransaction(doc, "RevitMCP: Batch Set Parameters", () =>
        {
            var changed = 0;
            var errors = new JArray();

            foreach (var element in elements)
            {
                foreach (var prop in values.Properties())
                {
                    var param = element.LookupParameter(prop.Name);
                    if (param == null)
                    {
                        errors.Add(new JObject { ["elementId"] = element.Id.Value, ["parameter"] = prop.Name, ["error"] = "parameter not found" });
                        continue;
                    }

                    if (TrySetParameterValue(param, prop.Value, out var error))
                    {
                        changed++;
                    }
                    else
                    {
                        errors.Add(new JObject { ["elementId"] = element.Id.Value, ["parameter"] = prop.Name, ["error"] = error });
                    }
                }
            }

            return new JObject
            {
                ["success"] = true,
                ["matchedElements"] = elements.Count,
                ["valuesApplied"] = changed,
                ["errors"] = errors,
            };
        });
    }

    private static JObject AuditFamilies(Document doc, JObject p)
    {
        var libPath = p["libraryPath"]?.Value<string>();

        var families = new FilteredElementCollector(doc)
            .OfClass(typeof(Family))
            .Cast<Family>()
            .ToList();

        // Count instances per family
        var instanceCounts = new FilteredElementCollector(doc)
            .OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>()
            .GroupBy(fi => fi.Symbol.Family.Id.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        var report = families.Select(f =>
        {
            var count = instanceCounts.GetValueOrDefault(f.Id.Value, 0);
            var isSystem = f.IsInPlace;
            var path = "";
            try { path = f.get_Parameter(BuiltInParameter.SYMBOL_FAMILY_NAME_PARAM)?.AsString() ?? ""; } catch { }

            return new JObject
            {
                ["id"]          = f.Id.Value,
                ["name"]        = f.Name,
                ["category"]    = f.FamilyCategory?.Name,
                ["instanceCount"]= count,
                ["isUnused"]    = count == 0,
                ["isInPlace"]   = isSystem,
                ["isExternal"]  = libPath != null && !string.IsNullOrEmpty(path) && !path.StartsWith(libPath, StringComparison.OrdinalIgnoreCase),
            };
        }).OrderBy(f => f["category"]!.ToString()).ThenBy(f => f["name"]!.ToString()).ToList();

        return new JObject
        {
            ["totalFamilies"]  = families.Count,
            ["unusedFamilies"] = report.Count(r => r["isUnused"]!.Value<bool>()),
            ["families"]       = new JArray(report),
        };
    }

    // ════════════════════════════════════════════════════════════════════════
    // V2 — CSV PARAMETER IMPORT
    // ════════════════════════════════════════════════════════════════════════

    private static JObject ImportParametersFromCsv(Document doc, JObject p)
    {
        var csvPath   = GetStr(p, "csvPath");
        var delimiter = p["delimiter"]?.Value<string>() ?? "comma";
        var dryRun    = p["dryRun"]?.Value<bool>() ?? false;

        char sep = delimiter switch { "tab" => '\t', "semicolon" => ';', _ => ',' };

        if (!File.Exists(csvPath)) throw new FileNotFoundException($"CSV not found: {csvPath}");

        var lines = File.ReadAllLines(csvPath, System.Text.Encoding.UTF8);
        if (lines.Length < 2) throw new InvalidOperationException("CSV must have a header row and at least one data row.");

        var headers = lines[0].Split(sep).Select(h => h.Trim('"')).ToArray();
        if (headers[0].ToLower() != "elementid") throw new InvalidOperationException("First column must be 'ElementId'.");

        var paramNames = headers.Skip(1).ToArray();
        var changes = new JArray();
        var errors  = new JArray();

        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var cols = line.Split(sep).Select(c => c.Trim('"')).ToArray();
            if (!int.TryParse(cols[0], out var eid)) { errors.Add($"Invalid ElementId: {cols[0]}"); continue; }

            var el = doc.GetElement(new ElementId(eid));
            if (el == null) { errors.Add($"Element {eid} not found"); continue; }

            for (int i = 0; i < paramNames.Length; i++)
            {
                var val = i + 1 < cols.Length ? cols[i + 1] : "";
                changes.Add(new JObject { ["elementId"] = eid, ["parameter"] = paramNames[i], ["value"] = val });
            }
        }

        if (dryRun)
            return new JObject { ["dryRun"] = true, ["changeCount"] = changes.Count, ["errors"] = errors, ["preview"] = changes };

        return WithTransaction(doc, "RevitMCP: Import CSV Parameters", () =>
        {
            int applied = 0;
            foreach (var c in changes.Cast<JObject>())
            {
                var el = doc.GetElement(new ElementId(c["elementId"]!.Value<int>()));
                var param = el?.LookupParameter(c["parameter"]!.Value<string>()!);
                if (param == null || param.IsReadOnly) continue;
                try
                {
                    switch (param.StorageType)
                    {
                        case StorageType.String:  param.Set(c["value"]!.Value<string>()); break;
                        case StorageType.Integer: param.Set(int.Parse(c["value"]!.Value<string>()!)); break;
                        case StorageType.Double:  param.Set(double.Parse(c["value"]!.Value<string>()!)); break;
                    }
                    applied++;
                }
                catch { errors.Add($"Failed to set {c["parameter"]} on {c["elementId"]}"); }
            }
            return new JObject { ["success"] = true, ["applied"] = applied, ["errors"] = errors };
        });
    }

    // ════════════════════════════════════════════════════════════════════════
    // V2 — AUTO SHEET POPULATION
    // ════════════════════════════════════════════════════════════════════════

    private static JObject AutoPopulateSheets(Document doc, JObject p)
    {
        var rules       = p["rules"]?.ToObject<JArray>() ?? throw new ArgumentException("rules required");
        var skipExisting = p["skipExistingSheets"]?.Value<bool>() ?? true;

        return WithTransaction(doc, "RevitMCP: Auto Populate Sheets", () =>
        {
            var allViews = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate && v.CanBePrinted)
                .ToList();

            var existingSheetNumbers = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Select(s => s.SheetNumber)
                .ToHashSet();

            // Default title block
            var defaultTb = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .FirstOrDefault();

            var created = new JArray();
            int sheetIndex = 0;

            foreach (var rule in rules.Cast<JObject>())
            {
                var pattern    = rule["viewNamePattern"]?.Value<string>() ?? "";
                var numPrefix  = rule["sheetNumberPrefix"]?.Value<string>() ?? "A-";
                var nameTemplate = rule["sheetNameTemplate"]?.Value<string>() ?? "{viewName}";
                var tbName     = rule["titleBlockName"]?.Value<string>();

                FamilySymbol? tb = defaultTb;
                if (tbName != null)
                    tb = new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_TitleBlocks)
                        .OfClass(typeof(FamilySymbol))
                        .Cast<FamilySymbol>()
                        .FirstOrDefault(s => s.Name == tbName) ?? defaultTb;

                var matchingViews = allViews
                    .Where(v => v.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase));

                foreach (var view in matchingViews)
                {
                    sheetIndex++;
                    var sheetNum  = $"{numPrefix}{sheetIndex:D2}";
                    var sheetName = nameTemplate.Replace("{viewName}", view.Name);

                    if (skipExisting && existingSheetNumbers.Contains(sheetNum))
                    {
                        created.Add(new JObject { ["skipped"] = true, ["sheetNumber"] = sheetNum, ["reason"] = "already exists" });
                        continue;
                    }

                    var sheet = tb != null
                        ? ViewSheet.Create(doc, tb.Id)
                        : ViewSheet.Create(doc, ElementId.InvalidElementId);

                    sheet.SheetNumber = sheetNum;
                    sheet.Name = sheetName;
                    existingSheetNumbers.Add(sheetNum);

                    // Place view centred on sheet
                    if (Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id))
                    {
                        Viewport.Create(doc, sheet.Id, view.Id, new XYZ(0.5, 0.35, 0));
                    }

                    created.Add(new JObject
                    {
                        ["sheetId"]     = sheet.Id.Value,
                        ["sheetNumber"] = sheetNum,
                        ["sheetName"]   = sheetName,
                        ["viewName"]    = view.Name,
                    });
                }
            }

            return new JObject { ["success"] = true, ["sheetsCreated"] = created.Count(c => c["skipped"] == null), ["details"] = created };
        });
    }
}
