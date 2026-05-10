using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Scratch2NET.Conversions
{
    // -------------------------------------------------------------------------
    //  BlockTranspiler.cs
    //
    //  Entry point:  BlockTranspiler.Transpile(projectJsonPath, outputFolder)
    //
    //  Reads project.json, builds an in-memory model of every target (sprite /
    //  stage), then emits one .cs file per sprite plus a Stage.cs that holds
    //  global variables and the broadcast bus.
    //
    //  Coordinate note:
    //    Scratch uses a centred system: X ∈ [-240,240], Y ∈ [-180,180].
    //    We store positions in Scratch-space and translate to screen-space only
    //    inside ScratchRuntime.cs during paint.
    // -------------------------------------------------------------------------

    public static class BlockTranspiler
    {
        // ── Public entry point ───────────────────────────────────────────────

        /// <summary>
        /// Transpiles a project.json file into C# source files written to
        /// <paramref name="outputFolder"/>.
        /// Returns a log of every action taken (for display in MainForm).
        /// </summary>
        public static List<string> Transpile(string projectJsonPath, string outputFolder)
        {
            var log = new List<string>();

            // 1. Parse JSON ───────────────────────────────────────────────────
            string raw = File.ReadAllText(projectJsonPath);
            JObject root = JObject.Parse(raw);
            JArray targets = (JArray)root["targets"];

            log.Add($"Loaded project.json — {targets.Count} target(s) found.");

            // 2. Collect global variable names from the Stage target ──────────
            var globalVars = new Dictionary<string, string>(); // id → sanitised C# name
            JObject stageTarget = targets.FirstOrDefault(t => (bool)t["isStage"]) as JObject;
            if (stageTarget != null)
            {
                foreach (var kv in (JObject)stageTarget["variables"])
                {
                    string varId = kv.Key;
                    string varName = (string)((JArray)kv.Value)[0];
                    globalVars[varId] = SanitiseName(varName);
                }
            }
            log.Add($"Global variables: {string.Join(", ", globalVars.Values)}");

            // 3. Collect broadcast names ──────────────────────────────────────
            var broadcasts = new Dictionary<string, string>(); // id → sanitised name
            if (stageTarget != null)
            {
                foreach (var kv in (JObject)stageTarget["broadcasts"])
                {
                    broadcasts[kv.Key] = SanitiseName((string)kv.Value);
                }
            }

            // 4. Emit Stage.cs ────────────────────────────────────────────────
            string stageSrc = EmitStage(stageTarget, globalVars, broadcasts, log);
            File.WriteAllText(Path.Combine(outputFolder, "Stage.cs"), stageSrc);
            log.Add("Wrote Stage.cs");

            // 5. Emit one .cs per non-stage sprite ───────────────────────────
            foreach (JObject target in targets.Where(t => !(bool)t["isStage"]))
            {
                string spriteName = SanitiseName((string)target["name"]);
                string src = EmitSprite(target, spriteName, globalVars, broadcasts, log);
                string fileName = spriteName + ".cs";
                File.WriteAllText(Path.Combine(outputFolder, fileName), src);
                log.Add($"Wrote {fileName}");
            }

            return log;
        }

        // ── Stage emitter ────────────────────────────────────────────────────

        private static string EmitStage(JObject target,
            Dictionary<string, string> globalVars,
            Dictionary<string, string> broadcasts,
            List<string> log)
        {
            var sb = new StringBuilder();
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using System.Drawing;");
            sb.AppendLine("using System.Threading.Tasks;");
            sb.AppendLine("using System.Windows.Forms;");
            sb.AppendLine("using Scratch2NET.Runtime;");
            sb.AppendLine();
            sb.AppendLine("namespace GeneratedProject");
            sb.AppendLine("{");
            sb.AppendLine("    /// <summary>Global stage state: variables, mouse, broadcasts.</summary>");
            sb.AppendLine("    public static class Stage");
            sb.AppendLine("    {");

            // Global variables
            sb.AppendLine("        // ── Global variables ─────────────────────────────────");
            foreach (var kv in globalVars)
            {
                sb.AppendLine($"        public static double {kv.Value} = 0;");
            }
            sb.AppendLine();

            // Mouse state (updated each frame by ScratchRuntime)
            sb.AppendLine("        // ── Input state (written by ScratchRuntime each frame) ─");
            sb.AppendLine("        public static bool   MouseDown = false;");
            sb.AppendLine("        public static double MouseX    = 0;");
            sb.AppendLine("        public static double MouseY    = 0;");
            sb.AppendLine();
            sb.AppendLine("        public static bool KeyPressed(string key)");
            sb.AppendLine("            => ScratchRuntime.IsKeyPressed(key);");
            sb.AppendLine();

            // Broadcast bus
            sb.AppendLine("        // ── Broadcast bus ────────────────────────────────────");
            sb.AppendLine("        private static readonly Dictionary<string, List<Func<Task>>> _listeners");
            sb.AppendLine("            = new Dictionary<string, List<Func<Task>>>(StringComparer.OrdinalIgnoreCase);");
            sb.AppendLine();
            sb.AppendLine("        public static void RegisterBroadcastListener(string name, Func<Task> handler)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (!_listeners.ContainsKey(name))");
            sb.AppendLine("                _listeners[name] = new List<Func<Task>>();");
            sb.AppendLine("            _listeners[name].Add(handler);");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        /// <summary>Fire all listeners for a broadcast and await them all.</summary>");
            sb.AppendLine("        public static async Task Broadcast(string name)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (!_listeners.ContainsKey(name)) return;");
            sb.AppendLine("            var tasks = new List<Task>();");
            sb.AppendLine("            foreach (var h in _listeners[name]) tasks.Add(h());");
            sb.AppendLine("            await Task.WhenAll(tasks);");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        // ── Sprite emitter ───────────────────────────────────────────────────

        private static string EmitSprite(JObject target, string spriteName,
            Dictionary<string, string> globalVars,
            Dictionary<string, string> broadcasts,
            List<string> log)
        {
            // Build costume name→index map from this sprite's costume array.
            // costumes are referenced by their friendly "name" field in blocks.
            var costumeIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var costumeFiles = new List<string>(); // friendly-name→md5ext mapping
            JArray costumes = (JArray)target["costumes"];
            for (int i = 0; i < costumes.Count; i++)
            {
                string cname = (string)costumes[i]["name"];
                costumeIndex[cname] = i;
                costumeFiles.Add((string)costumes[i]["md5ext"]); // hash-named file
            }

            // Build sound name→file map
            var soundFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            JArray sounds = (JArray)target["sounds"];
            foreach (JToken s in sounds)
            {
                soundFiles[(string)s["name"]] = (string)s["md5ext"];
            }

            // Block dictionary for this sprite
            JObject blocks = (JObject)target["blocks"];

            // Find all hat (topLevel) blocks.
            // Cast to IEnumerable explicitly so LINQ resolves the Enumerable
            // overload of Where rather than the ambiguous Queryable one.
            var hats = ((IEnumerable<KeyValuePair<string, JToken>>)blocks)
                .Where(kv =>
                {
                    var b = (JObject)kv.Value;
                    return (bool)b["topLevel"] && !(bool)b["shadow"];
                })
                .Select(kv => new HatEntry(kv.Key, (JObject)kv.Value))
                .ToList();

            log.Add($"  {spriteName}: {hats.Count} hat block(s), {costumeIndex.Count} costume(s)");

            var ctx = new EmitContext
            {
                Blocks = blocks,
                GlobalVars = globalVars,
                Broadcasts = broadcasts,
                SpriteName = spriteName,
                CostumeIndex = costumeIndex,
                Log = log
            };

            var sb = new StringBuilder();
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Threading.Tasks;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using Scratch2NET.Runtime;");
            sb.AppendLine();
            sb.AppendLine("namespace GeneratedProject");
            sb.AppendLine("{");
            sb.AppendLine($"    public class {spriteName} : ScratchSprite");
            sb.AppendLine("    {");

            // Constructor — load costumes by md5ext filename, register broadcasts
            sb.AppendLine($"        public {spriteName}(ScratchStage stage) : base(stage)");
            sb.AppendLine("        {");
            sb.AppendLine("            // Costumes loaded by asset name (md5ext → friendly name mapping)");
            for (int i = 0; i < costumes.Count; i++)
            {
                string friendlyName = (string)costumes[i]["name"];
                string md5ext = (string)costumes[i]["md5ext"];
                // rotationCenterX/Y used to offset drawing so the sprite rotates
                // around its actual centre rather than the bitmap corner.
                int rcx = costumes[i]["rotationCenterX"] != null
                    ? (int)costumes[i]["rotationCenterX"] : 0;
                int rcy = costumes[i]["rotationCenterY"] != null
                    ? (int)costumes[i]["rotationCenterY"] : 0;
                // bitmapResolution: Scratch stores @2x bitmaps; we halve the
                // rotation center to get the logical pixel origin.
                int res = costumes[i]["bitmapResolution"] != null
                    ? (int)costumes[i]["bitmapResolution"] : 1;
                sb.AppendLine($"            AddCostume(\"{friendlyName}\", \"{md5ext}\", {rcx / res}, {rcy / res});");
            }
            sb.AppendLine();
            sb.AppendLine("            // Sounds");
            foreach (var kv in soundFiles)
            {
                sb.AppendLine($"            AddSound(\"{kv.Key}\", \"{kv.Value}\");");
            }
            sb.AppendLine();

            // Register broadcast listeners for any whenbroadcastreceived hats
            foreach (HatEntry bcastHat in hats.Where(h => (string)h.Block["opcode"] == "event_whenbroadcastreceived"))
            {
                string bcastName = (string)bcastHat.Block["fields"]["BROADCAST_OPTION"][0];
                string sanitised = SanitiseName(bcastName);
                sb.AppendLine($"            Stage.RegisterBroadcastListener(\"{bcastName}\", () => OnBroadcast_{sanitised}());");
            }

            sb.AppendLine("        }");
            sb.AppendLine();

            // Initial sprite state from JSON
            sb.AppendLine("        public override void InitialState()");
            sb.AppendLine("        {");
            sb.AppendLine($"            X         = {(double)target["x"]};");
            sb.AppendLine($"            Y         = {(double)target["y"]};");
            sb.AppendLine($"            Direction = {(double)target["direction"]};");
            sb.AppendLine($"            Size      = {(double)target["size"]};");
            sb.AppendLine($"            Visible   = {((bool)target["visible"]).ToString().ToLower()};");
            sb.AppendLine($"            CostumeIndex = {(int)target["currentCostume"]};");
            sb.AppendLine("        }");
            sb.AppendLine();

            // Emit each hat script as its own async method
            foreach (HatEntry hat in hats)
            {
                EmitScript(hat.Id, hat.Block, ctx, sb);
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        // ── Script emitter ───────────────────────────────────────────────────

        private static void EmitScript(string hatId, JObject hatBlock,
            EmitContext ctx, StringBuilder sb)
        {
            string opcode = (string)hatBlock["opcode"];
            var def = BlockDefinitions.Get(opcode);

            // Build the method signature from the hat opcode
            string methodSig;
            switch (opcode)
            {
                case "event_whenflagclicked":
                    methodSig = "public override async Task OnFlagClicked()";
                    break;
                case "event_whenbroadcastreceived":
                    string bName = SanitiseName((string)hatBlock["fields"]["BROADCAST_OPTION"][0]);
                    methodSig = $"public async Task OnBroadcast_{bName}()";
                    break;
                case "control_start_as_clone":
                    methodSig = "public override async Task OnStartAsClone()";
                    break;
                default:
                    methodSig = $"/* Unhandled hat: {opcode} */";
                    break;
            }

            sb.AppendLine($"        {methodSig}");
            sb.AppendLine("        {");
            sb.AppendLine("            bool _scriptStopped = false;");
            sb.AppendLine("            _ = _scriptStopped; // suppress unused warning if no stop block");

            // Walk the script chain starting from hat's "next" block
            string nextId = (string)hatBlock["next"];
            if (nextId != null)
            {
                EmitBlockChain(nextId, ctx, sb, indentLevel: 3);
            }

            sb.AppendLine("        }");
            sb.AppendLine();
        }

        // ── Block chain walker ───────────────────────────────────────────────

        /// <summary>
        /// Walks a linear chain of blocks (following "next" links) and emits
        /// C# statements for each one.  C-shape blocks recurse for their
        /// SUBSTACK children.
        /// </summary>
        private static void EmitBlockChain(string startId, EmitContext ctx,
            StringBuilder sb, int indentLevel)
        {
            string currentId = startId;
            while (currentId != null)
            {
                JObject block = (JObject)ctx.Blocks[currentId];
                string opcode = (string)block["opcode"];

                // Shadow/menu blocks should never appear as top-level stack
                // blocks — skip them silently.
                if ((bool)block["shadow"])
                {
                    currentId = (string)block["next"];
                    continue;
                }

                EmitSingleBlock(currentId, block, opcode, ctx, sb, indentLevel);
                currentId = (string)block["next"];
            }
        }

        // ── Single block emitter ─────────────────────────────────────────────

        private static void EmitSingleBlock(string blockId, JObject block,
            string opcode, EmitContext ctx, StringBuilder sb, int indent)
        {
            string pad = new string(' ', indent * 4);
            var def = BlockDefinitions.Get(opcode);

            if (def == null)
            {
                sb.AppendLine($"{pad}/* TODO: unsupported opcode '{opcode}' */");
                return;
            }

            switch (opcode)
            {
                // ── Motion ───────────────────────────────────────────────────

                case "motion_movesteps":
                    {
                        string steps = ResolveInput(block, "STEPS", ctx);
                        sb.AppendLine($"{pad}MoveSteps({steps});");
                        sb.AppendLine($"{pad}await Task.Yield();");
                        break;
                    }
                case "motion_turnleft":
                    {
                        string deg = ResolveInput(block, "DEGREES", ctx);
                        sb.AppendLine($"{pad}TurnLeft({deg});");
                        break;
                    }
                case "motion_turnright":
                    {
                        string deg = ResolveInput(block, "DEGREES", ctx);
                        sb.AppendLine($"{pad}TurnRight({deg});");
                        break;
                    }
                case "motion_pointindirection":
                    {
                        string dir = ResolveInput(block, "DIRECTION", ctx);
                        sb.AppendLine($"{pad}PointInDirection({dir});");
                        break;
                    }
                case "motion_pointtowards":
                    {
                        string towards = ResolveInput(block, "TOWARDS", ctx);
                        sb.AppendLine($"{pad}PointTowards({towards});");
                        break;
                    }
                case "motion_gotoxy":
                    {
                        string x = ResolveInput(block, "X", ctx);
                        string y = ResolveInput(block, "Y", ctx);
                        sb.AppendLine($"{pad}GoToXY({x}, {y});");
                        break;
                    }
                case "motion_glidesecstoxy":
                    {
                        string secs = ResolveInput(block, "SECS", ctx);
                        string x = ResolveInput(block, "X", ctx);
                        string y = ResolveInput(block, "Y", ctx);
                        sb.AppendLine($"{pad}await GlideSecsToXY({secs}, {x}, {y});");
                        break;
                    }
                case "motion_ifonedgebounce":
                    sb.AppendLine($"{pad}IfOnEdgeBounce();");
                    break;

                // ── Looks ────────────────────────────────────────────────────

                case "looks_switchcostumeto":
                    {
                        // Input is a shadow "looks_costume" block whose COSTUME
                        // field holds the friendly name string.
                        string costumeName = ResolveInput(block, "COSTUME", ctx);
                        sb.AppendLine($"{pad}SwitchCostume({costumeName});");
                        break;
                    }
                case "looks_setsizeto":
                    {
                        string size = ResolveInput(block, "SIZE", ctx);
                        sb.AppendLine($"{pad}SetSize({size});");
                        break;
                    }
                case "looks_gotofrontback":
                    {
                        string fb = (string)block["fields"]["FRONT_BACK"][0];
                        sb.AppendLine($"{pad}GoToFrontBack(\"{fb}\");");
                        break;
                    }

                // ── Events ───────────────────────────────────────────────────

                case "event_broadcast":
                    {
                        // Input [11, "name", "id"] — we want the human name.
                        string bName = ResolveBroadcastInput(block, ctx);
                        sb.AppendLine($"{pad}await Stage.Broadcast(\"{bName}\");");
                        break;
                    }

                // ── Data ─────────────────────────────────────────────────────

                case "data_setvariableto":
                    {
                        string varName = ResolveVariableName(block, ctx);
                        string val = ResolveInput(block, "VALUE", ctx);
                        sb.AppendLine($"{pad}{varName} = ScratchRuntime.ToNumber({val});");
                        break;
                    }
                case "data_changevariableby":
                    {
                        string varName = ResolveVariableName(block, ctx);
                        string val = ResolveInput(block, "VALUE", ctx);
                        sb.AppendLine($"{pad}{varName} += ScratchRuntime.ToNumber({val});");
                        break;
                    }

                // ── Sensing ──────────────────────────────────────────────────

                case "sensing_setdragmode":
                    // No-op in desktop runtime — emit a comment only.
                    sb.AppendLine($"{pad}/* drag mode not applicable in desktop runtime */");
                    break;

                // ── Control ──────────────────────────────────────────────────

                case "control_wait":
                    {
                        string dur = ResolveInput(block, "DURATION", ctx);
                        sb.AppendLine($"{pad}await Task.Delay((int)(({dur}) * 1000));");
                        break;
                    }
                case "control_forever":
                    {
                        sb.AppendLine($"{pad}while (!_cloneDeleted && !_scriptStopped)");
                        sb.AppendLine($"{pad}{{");
                        string substackId = ResolveSubstack(block, "SUBSTACK");
                        if (substackId != null)
                            EmitBlockChain(substackId, ctx, sb, indent + 1);
                        // Yield once per loop iteration to keep the UI responsive.
                        sb.AppendLine($"{pad}    await Task.Yield();");
                        sb.AppendLine($"{pad}}}");
                        break;
                    }
                case "control_repeat":
                    {
                        string times = ResolveInput(block, "TIMES", ctx);
                        sb.AppendLine($"{pad}for (int _i = 0; _i < (int)({times}); _i++)");
                        sb.AppendLine($"{pad}{{");
                        string substackId = ResolveSubstack(block, "SUBSTACK");
                        if (substackId != null)
                            EmitBlockChain(substackId, ctx, sb, indent + 1);
                        sb.AppendLine($"{pad}    await Task.Yield();");
                        sb.AppendLine($"{pad}}}");
                        break;
                    }
                case "control_if":
                    {
                        string cond = ResolveInput(block, "CONDITION", ctx);
                        sb.AppendLine($"{pad}if ({cond})");
                        sb.AppendLine($"{pad}{{");
                        string substackId = ResolveSubstack(block, "SUBSTACK");
                        if (substackId != null)
                            EmitBlockChain(substackId, ctx, sb, indent + 1);
                        sb.AppendLine($"{pad}}}");
                        break;
                    }
                case "control_if_else":
                    {
                        string cond = ResolveInput(block, "CONDITION", ctx);
                        sb.AppendLine($"{pad}if ({cond})");
                        sb.AppendLine($"{pad}{{");
                        string substack1 = ResolveSubstack(block, "SUBSTACK");
                        if (substack1 != null)
                            EmitBlockChain(substack1, ctx, sb, indent + 1);
                        sb.AppendLine($"{pad}}}");
                        sb.AppendLine($"{pad}else");
                        sb.AppendLine($"{pad}{{");
                        string substack2 = ResolveSubstack(block, "SUBSTACK2");
                        if (substack2 != null)
                            EmitBlockChain(substack2, ctx, sb, indent + 1);
                        sb.AppendLine($"{pad}}}");
                        break;
                    }
                case "control_stop":
                    {
                        string stopOption = (string)block["fields"]["STOP_OPTION"][0];
                        switch (stopOption)
                        {
                            case "this script":
                                sb.AppendLine($"{pad}_scriptStopped = true;");
                                sb.AppendLine($"{pad}return;");
                                break;
                            case "all":
                                sb.AppendLine($"{pad}Stage.StopAll();");
                                sb.AppendLine($"{pad}return;");
                                break;
                            case "other scripts in sprite":
                                sb.AppendLine($"{pad}/* TODO: stop other scripts — requires task tracking */");
                                break;
                            default:
                                sb.AppendLine($"{pad}/* TODO: stop option '{stopOption}' */");
                                break;
                        }
                        break;
                    }
                case "control_create_clone_of":
                    {
                        // CLONE_OPTION shadow: "_myself_" or a sprite name
                        string cloneOf = ResolveCloneTarget(block, ctx);
                        sb.AppendLine($"{pad}CreateClone({cloneOf});");
                        break;
                    }
                case "control_delete_this_clone":
                    sb.AppendLine($"{pad}DeleteClone();");
                    sb.AppendLine($"{pad}return;");
                    break;

                // control_start_as_clone is a hat — handled by EmitScript,
                // should never appear mid-chain.

                default:
                    sb.AppendLine($"{pad}/* TODO: unhandled opcode '{opcode}' */");
                    break;
            }
        }

        // ── Input resolver ───────────────────────────────────────────────────
        //
        //  Scratch input arrays look like:
        //    [inputType, valueArray]          — literal / variable / block ref
        //
        //  valueArray types:
        //    [4, "10"]         — number literal
        //    [5, "3.3"]        — positive number literal
        //    [6, "100"]        — positive integer literal
        //    [7, "90"]         — integer literal
        //    [8, "90"]         — angle literal
        //    [10, "hello"]     — string literal
        //    [11, "name","id"] — broadcast reference (name, id)
        //    [12, "SPEED","id"]— variable reference (name, id)
        //
        //  If the input contains a block id instead of a value array, that
        //  block is a reporter and we resolve it recursively.

        private static string ResolveInput(JObject block, string inputName,
            EmitContext ctx)
        {
            JObject inputs = (JObject)block["inputs"];
            if (inputs == null || inputs[inputName] == null)
                return "0";

            JArray inputArr = (JArray)inputs[inputName];
            // inputArr[0] = input type (1=value, 2=block only, 3=block+default)
            // inputArr[1] = either a block id string OR a value array

            JToken inner = inputArr[1];

            // If inner is a string it's a block id — resolve that block
            if (inner.Type == JTokenType.String)
            {
                string refId = (string)inner;
                if (ctx.Blocks[refId] != null)
                    return ResolveReporter(refId, ctx);
                return "0";
            }

            // Otherwise it's a value array [type, value]
            if (inner.Type == JTokenType.Array)
            {
                JArray val = (JArray)inner;
                int valType = (int)val[0];
                string rawVal = val.Count > 1 ? (string)val[1] : "";

                switch (valType)
                {
                    case 4:
                    case 5:
                    case 6:
                    case 7:
                    case 8:
                        // Numeric — return as a C# double literal
                        return ToDoubleLiteral(rawVal);
                    case 10:
                        // String literal
                        return $"\"{EscapeString(rawVal)}\"";
                    case 11:
                        // Broadcast reference — return name string
                        return $"\"{EscapeString(rawVal)}\"";
                    case 12:
                        // Variable reference — return the sanitised C# field name
                        return ResolveVariableRef(rawVal, (string)val[1], ctx);
                    default:
                        return $"\"{EscapeString(rawVal)}\"";
                }
            }

            return "0";
        }

        /// <summary>
        /// Resolves a reporter block (one that returns a value) to a C#
        /// expression string.
        /// </summary>
        private static string ResolveReporter(string blockId, EmitContext ctx)
        {
            JObject block = (JObject)ctx.Blocks[blockId];
            string opcode = (string)block["opcode"];

            switch (opcode)
            {
                // ── Motion reporters ─────────────────────────────────────────
                case "motion_direction":
                    return "Direction";

                case "motion_xposition":
                    return "X";

                case "motion_yposition":
                    return "Y";

                // ── Looks shadows (menu) ──────────────────────────────────────
                case "looks_costume":
                    {
                        string cname = (string)block["fields"]["COSTUME"][0];
                        // Return the integer index so SwitchCostume(int) is called.
                        if (ctx.CostumeIndex.TryGetValue(cname, out int idx))
                            return idx.ToString();
                        return $"\"{EscapeString(cname)}\"";
                    }

                // ── Motion menu shadows ───────────────────────────────────────
                case "motion_pointtowards_menu":
                    {
                        string towards = (string)block["fields"]["TOWARDS"][0];
                        // "_mouse_" is the only value we currently support.
                        return towards == "_mouse_" ? "\"_mouse_\"" : $"\"{EscapeString(towards)}\"";
                    }

                case "control_create_clone_of_menu":
                    {
                        string opt = (string)block["fields"]["CLONE_OPTION"][0];
                        return opt == "_myself_" ? "this" : $"new {SanitiseName(opt)}(Stage)";
                    }

                // ── Operators ─────────────────────────────────────────────────
                case "operator_add":
                    {
                        string a = ResolveInput(block, "NUM1", ctx);
                        string b = ResolveInput(block, "NUM2", ctx);
                        return $"({a} + {b})";
                    }
                case "operator_subtract":
                    {
                        string a = ResolveInput(block, "NUM1", ctx);
                        string b = ResolveInput(block, "NUM2", ctx);
                        return $"({a} - {b})";
                    }
                case "operator_multiply":
                    {
                        string a = ResolveInput(block, "NUM1", ctx);
                        string b = ResolveInput(block, "NUM2", ctx);
                        return $"({a} * {b})";
                    }
                case "operator_divide":
                    {
                        string a = ResolveInput(block, "NUM1", ctx);
                        string b = ResolveInput(block, "NUM2", ctx);
                        return $"({a} / {b})";
                    }
                case "operator_mod":
                    {
                        string a = ResolveInput(block, "NUM1", ctx);
                        string b = ResolveInput(block, "NUM2", ctx);
                        return $"({a} % {b})";
                    }
                case "operator_gt":
                    {
                        string a = ResolveInput(block, "OPERAND1", ctx);
                        string b = ResolveInput(block, "OPERAND2", ctx);
                        return $"({a} > {b})";
                    }
                case "operator_lt":
                    {
                        string a = ResolveInput(block, "OPERAND1", ctx);
                        string b = ResolveInput(block, "OPERAND2", ctx);
                        return $"({a} < {b})";
                    }
                case "operator_equals":
                    {
                        string a = ResolveInput(block, "OPERAND1", ctx);
                        string b = ResolveInput(block, "OPERAND2", ctx);
                        return $"ScratchRuntime.Equals({a}, {b})";
                    }
                case "operator_not":
                    {
                        string op = ResolveInput(block, "OPERAND", ctx);
                        return $"(!({op}))";
                    }
                case "operator_and":
                    {
                        string a = ResolveInput(block, "OPERAND1", ctx);
                        string b = ResolveInput(block, "OPERAND2", ctx);
                        return $"({a} && {b})";
                    }
                case "operator_or":
                    {
                        string a = ResolveInput(block, "OPERAND1", ctx);
                        string b = ResolveInput(block, "OPERAND2", ctx);
                        return $"({a} || {b})";
                    }
                case "operator_random":
                    {
                        string a = ResolveInput(block, "FROM", ctx);
                        string b = ResolveInput(block, "TO", ctx);
                        return $"ScratchRuntime.PickRandom({a}, {b})";
                    }
                case "operator_mathop":
                    {
                        string mathOp = (string)block["fields"]["OPERATOR"][0];
                        string num = ResolveInput(block, "NUM", ctx);
                        return $"ScratchRuntime.MathOp(\"{mathOp}\", {num})";
                    }
                case "operator_join":
                    {
                        string a = ResolveInput(block, "STRING1", ctx);
                        string b = ResolveInput(block, "STRING2", ctx);
                        return $"({a}.ToString() + {b}.ToString())";
                    }

                // ── Sensing reporters ─────────────────────────────────────────
                case "sensing_mousedown":
                    return "Stage.MouseDown";

                case "sensing_mousex":
                    return "Stage.MouseX";

                case "sensing_mousey":
                    return "Stage.MouseY";

                case "sensing_keypressed":
                    {
                        string key = ResolveInput(block, "KEY_OPTION", ctx);
                        return $"Stage.KeyPressed({key})";
                    }

                // ── Data: variable reference ───────────────────────────────────
                case "data_variable":
                    {
                        string varName = (string)block["fields"]["VARIABLE"][0];
                        return ResolveVariableRef(varName, null, ctx);
                    }

                default:
                    ctx.Log.Add($"  WARNING: unresolved reporter '{opcode}' — defaulting to 0");
                    return "0 /* unresolved reporter: " + opcode + " */";
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private static string ResolveSubstack(JObject block, string key)
        {
            JObject inputs = (JObject)block["inputs"];
            if (inputs == null || inputs[key] == null) return null;
            JArray arr = (JArray)inputs[key];
            // [2, "blockId"]
            return arr.Count > 1 && arr[1].Type == JTokenType.String
                ? (string)arr[1] : null;
        }

        private static string ResolveBroadcastInput(JObject block, EmitContext ctx)
        {
            // BROADCAST_INPUT is [1, [11, "name", "id"]]
            JArray inputArr = (JArray)((JObject)block["inputs"])["BROADCAST_INPUT"];
            JToken inner = inputArr[1];
            if (inner.Type == JTokenType.Array)
            {
                JArray val = (JArray)inner;
                return (string)val[1]; // human-readable name
            }
            // Could be a reporter block that produces the name dynamically
            if (inner.Type == JTokenType.String)
                return (string)ResolveReporter((string)inner, ctx);
            return "INIT";
        }

        private static string ResolveVariableName(JObject block, EmitContext ctx)
        {
            JObject fields = (JObject)block["fields"];
            JArray varField = (JArray)fields["VARIABLE"];
            string varRawName = (string)varField[0];
            string varId = (string)varField[1];
            return ResolveVariableRef(varRawName, varId, ctx);
        }

        /// <summary>
        /// Returns the C# expression for reading a Scratch variable.
        /// Global (stage) variables are accessed via Stage.VarName.
        /// Local sprite variables are accessed as this.VarName.
        /// </summary>
        private static string ResolveVariableRef(string rawName, string varId,
            EmitContext ctx)
        {
            string safe = SanitiseName(rawName);
            // Check if it's in the global set
            if (ctx.GlobalVars.ContainsValue(safe) ||
                (varId != null && ctx.GlobalVars.ContainsKey(varId)))
                return $"Stage.{safe}";
            // Otherwise treat as sprite-local
            return safe;
        }

        private static string ResolveCloneTarget(JObject block, EmitContext ctx)
        {
            JObject inputs = (JObject)block["inputs"];
            if (inputs?["CLONE_OPTION"] == null) return "this";

            JArray arr = (JArray)inputs["CLONE_OPTION"];
            JToken inner = arr[1];
            if (inner.Type == JTokenType.String)
                return ResolveReporter((string)inner, ctx);
            return "this";
        }

        /// <summary>
        /// Converts a raw number string to a C# double literal.
        /// Handles integers cleanly (no trailing .0 for whole numbers).
        /// </summary>
        private static string ToDoubleLiteral(string raw)
        {
            if (double.TryParse(raw,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out double d))
            {
                // If it's a whole number, emit it without decimal to keep
                // generated code readable.
                if (d == Math.Floor(d) && !double.IsInfinity(d))
                    return ((long)d).ToString();
                return d.ToString("G", System.Globalization.CultureInfo.InvariantCulture);
            }
            // Fallback: emit as string that will be coerced at runtime
            return $"ScratchRuntime.ToNumber(\"{EscapeString(raw)}\")";
        }

        /// <summary>
        /// Converts a Scratch name (which can contain spaces, special chars,
        /// emoji, etc.) to a valid C# identifier.
        /// </summary>
        public static string SanitiseName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "_unnamed";
            // Replace any non-alphanumeric, non-underscore char with '_'
            string safe = Regex.Replace(name, @"[^\w]", "_");
            // Identifiers cannot start with a digit
            if (char.IsDigit(safe[0])) safe = "_" + safe;
            return safe;
        }

        private static string EscapeString(string s)
            => s?.Replace("\\", "\\\\").Replace("\"", "\\\"") ?? "";
    }

    // ── Simple hat entry — replaces anonymous tuple for .NET 4.8.1 compat ───

    internal class HatEntry
    {
        public string Id { get; }
        public JObject Block { get; }
        public HatEntry(string id, JObject block) { Id = id; Block = block; }
    }

    // ── Emit context (passed down through recursive emitters) ────────────────

    internal class EmitContext
    {
        public JObject Blocks { get; set; }
        /// <summary>Variable id → sanitised C# name (global/stage variables).</summary>
        public Dictionary<string, string> GlobalVars { get; set; }
        /// <summary>Broadcast id → sanitised name.</summary>
        public Dictionary<string, string> Broadcasts { get; set; }
        public string SpriteName { get; set; }
        /// <summary>Friendly costume name → index in the costume array.</summary>
        public Dictionary<string, int> CostumeIndex { get; set; }
        public List<string> Log { get; set; }
    }
}