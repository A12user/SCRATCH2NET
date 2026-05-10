using System.Collections.Generic;

namespace Scratch2NET.Conversions
{
    // -------------------------------------------------------------------------
    //  BlockDefinitions.cs
    //  Pure data layer — no logic. Maps every Scratch opcode we support to a
    //  BlockDef describing its category, how many inputs it takes, whether it
    //  is a reporter (returns a value), a hat (starts a script), a C-shape
    //  (contains a substack), and a short human-readable C# pattern used by
    //  the transpiler as a code-generation hint.
    //
    //  Pattern tokens:
    //    {0}, {1}, {2}  — positional inputs resolved by the transpiler
    //    {SELF}         — the sprite instance ("this" in generated code)
    //    {STAGE}        — the stage static class
    //  -------------------------------------------------------------------------

    public enum BlockCategory
    {
        Event,
        Motion,
        Looks,
        Sound,
        Control,
        Sensing,
        Operator,
        Data,
        Unknown
    }

    public enum BlockShape
    {
        /// <summary>Hat block — starts a script, topLevel = true.</summary>
        Hat,
        /// <summary>Stack block — does something, has optional next.</summary>
        Stack,
        /// <summary>C-shape block — contains a SUBSTACK (e.g. if, repeat, forever).</summary>
        CShape,
        /// <summary>Reporter — returns a value, used inside inputs.</summary>
        Reporter,
        /// <summary>Boolean reporter — returns true/false.</summary>
        Boolean,
        /// <summary>Shadow / menu block — provides a dropdown value.</summary>
        Shadow
    }

    public class BlockDef
    {
        /// <summary>Scratch opcode string, e.g. "motion_movesteps".</summary>
        public string Opcode { get; }

        public BlockCategory Category { get; }
        public BlockShape Shape { get; }

        /// <summary>
        /// C# code pattern. Tokens are replaced by the transpiler.
        /// For hats this is the method signature stub.
        /// For reporters this is the expression.
        /// </summary>
        public string CSharpPattern { get; }

        /// <summary>
        /// True if this block needs to be awaited (i.e. involves time or
        /// yielding — wait, glide, forever, repeat).
        /// </summary>
        public bool IsAsync { get; }

        public BlockDef(string opcode, BlockCategory category, BlockShape shape,
                        string csharpPattern, bool isAsync = false)
        {
            Opcode = opcode;
            Category = category;
            Shape = shape;
            CSharpPattern = csharpPattern;
            IsAsync = isAsync;
        }
    }

    public static class BlockDefinitions
    {
        // Key = opcode string.  Built once at startup.
        public static readonly Dictionary<string, BlockDef> All =
            new Dictionary<string, BlockDef>
            {
                // -----------------------------------------------------------------
                //  EVENTS
                // -----------------------------------------------------------------

                ["event_whenflagclicked"] = new BlockDef(
                "event_whenflagclicked", BlockCategory.Event, BlockShape.Hat,
                "public async Task OnFlagClicked()"),

                ["event_broadcast"] = new BlockDef(
                "event_broadcast", BlockCategory.Event, BlockShape.Stack,
                "{STAGE}.Broadcast({0})"),

                ["event_whenbroadcastreceived"] = new BlockDef(
                "event_whenbroadcastreceived", BlockCategory.Event, BlockShape.Hat,
                "public async Task OnBroadcast_{0}()"),

                // -----------------------------------------------------------------
                //  MOTION
                // -----------------------------------------------------------------

                ["motion_movesteps"] = new BlockDef(
                "motion_movesteps", BlockCategory.Motion, BlockShape.Stack,
                "{SELF}.MoveSteps({0})"),

                ["motion_turnleft"] = new BlockDef(
                "motion_turnleft", BlockCategory.Motion, BlockShape.Stack,
                "{SELF}.TurnLeft({0})"),

                ["motion_turnright"] = new BlockDef(
                "motion_turnright", BlockCategory.Motion, BlockShape.Stack,
                "{SELF}.TurnRight({0})"),

                ["motion_pointindirection"] = new BlockDef(
                "motion_pointindirection", BlockCategory.Motion, BlockShape.Stack,
                "{SELF}.PointInDirection({0})"),

                ["motion_pointtowards"] = new BlockDef(
                "motion_pointtowards", BlockCategory.Motion, BlockShape.Stack,
                "{SELF}.PointTowards({0})"),

                // menu shadow — value resolved inline by transpiler
                ["motion_pointtowards_menu"] = new BlockDef(
                "motion_pointtowards_menu", BlockCategory.Motion, BlockShape.Shadow,
                "{0}"),

                ["motion_gotoxy"] = new BlockDef(
                "motion_gotoxy", BlockCategory.Motion, BlockShape.Stack,
                "{SELF}.GoToXY({0}, {1})"),

                ["motion_glidesecstoxy"] = new BlockDef(
                "motion_glidesecstoxy", BlockCategory.Motion, BlockShape.Stack,
                "await {SELF}.GlideSecsToXY({0}, {1}, {2})", isAsync: true),

                ["motion_ifonedgebounce"] = new BlockDef(
                "motion_ifonedgebounce", BlockCategory.Motion, BlockShape.Stack,
                "{SELF}.IfOnEdgeBounce()"),

                // Reporter — returns the sprite's current direction as a double.
                ["motion_direction"] = new BlockDef(
                "motion_direction", BlockCategory.Motion, BlockShape.Reporter,
                "{SELF}.Direction"),

                // -----------------------------------------------------------------
                //  LOOKS
                // -----------------------------------------------------------------

                ["looks_switchcostumeto"] = new BlockDef(
                "looks_switchcostumeto", BlockCategory.Looks, BlockShape.Stack,
                "{SELF}.SwitchCostume({0})"),

                // Shadow/menu — resolved to a string literal by the transpiler.
                ["looks_costume"] = new BlockDef(
                "looks_costume", BlockCategory.Looks, BlockShape.Shadow,
                "{0}"),

                ["looks_setsizeto"] = new BlockDef(
                "looks_setsizeto", BlockCategory.Looks, BlockShape.Stack,
                "{SELF}.SetSize({0})"),

                ["looks_gotofrontback"] = new BlockDef(
                "looks_gotofrontback", BlockCategory.Looks, BlockShape.Stack,
                "{SELF}.GoToFrontBack(\"{0}\")"),

                // -----------------------------------------------------------------
                //  SOUND  (minimal — pop.wav playback via NAudio)
                // -----------------------------------------------------------------

                ["sound_play"] = new BlockDef(
                "sound_play", BlockCategory.Sound, BlockShape.Stack,
                "{SELF}.PlaySound({0})"),

                ["sound_playuntildone"] = new BlockDef(
                "sound_playuntildone", BlockCategory.Sound, BlockShape.Stack,
                "await {SELF}.PlaySoundUntilDone({0})", isAsync: true),

                ["sound_stopallsounds"] = new BlockDef(
                "sound_stopallsounds", BlockCategory.Sound, BlockShape.Stack,
                "ScratchRuntime.StopAllSounds()"),

                // -----------------------------------------------------------------
                //  CONTROL
                // -----------------------------------------------------------------

                ["control_wait"] = new BlockDef(
                "control_wait", BlockCategory.Control, BlockShape.Stack,
                "await Task.Delay((int)(({0}) * 1000))", isAsync: true),

                ["control_repeat"] = new BlockDef(
                "control_repeat", BlockCategory.Control, BlockShape.CShape,
                "for (int _i = 0; _i < (int)({0}); _i++)",
                isAsync: true),

                ["control_forever"] = new BlockDef(
                "control_forever", BlockCategory.Control, BlockShape.CShape,
                "while (!_cloneDeleted && !_scriptStopped)",
                isAsync: true),

                ["control_if"] = new BlockDef(
                "control_if", BlockCategory.Control, BlockShape.CShape,
                "if ({0})"),

                ["control_if_else"] = new BlockDef(
                "control_if_else", BlockCategory.Control, BlockShape.CShape,
                "if ({0})"),  // transpiler adds else branch separately

                ["control_stop"] = new BlockDef(
                "control_stop", BlockCategory.Control, BlockShape.Stack,
                // STOP_OPTION resolved by transpiler: "this script" / "all" / "other scripts"
                "{STOP}"),

                ["control_create_clone_of"] = new BlockDef(
                "control_create_clone_of", BlockCategory.Control, BlockShape.Stack,
                "{SELF}.CreateClone({0})"),

                ["control_create_clone_of_menu"] = new BlockDef(
                "control_create_clone_of_menu", BlockCategory.Control, BlockShape.Shadow,
                "{0}"),

                ["control_start_as_clone"] = new BlockDef(
                "control_start_as_clone", BlockCategory.Control, BlockShape.Hat,
                "public async Task OnStartAsClone()"),

                ["control_delete_this_clone"] = new BlockDef(
                "control_delete_this_clone", BlockCategory.Control, BlockShape.Stack,
                "{SELF}.DeleteClone(); return;"),

                // -----------------------------------------------------------------
                //  SENSING
                // -----------------------------------------------------------------

                ["sensing_mousedown"] = new BlockDef(
                "sensing_mousedown", BlockCategory.Sensing, BlockShape.Boolean,
                "{STAGE}.MouseDown"),

                ["sensing_mousex"] = new BlockDef(
                "sensing_mousex", BlockCategory.Sensing, BlockShape.Reporter,
                "{STAGE}.MouseX"),

                ["sensing_mousey"] = new BlockDef(
                "sensing_mousey", BlockCategory.Sensing, BlockShape.Reporter,
                "{STAGE}.MouseY"),

                ["sensing_keypressed"] = new BlockDef(
                "sensing_keypressed", BlockCategory.Sensing, BlockShape.Boolean,
                "{STAGE}.KeyPressed({0})"),

                // Not meaningful in .NET desktop — we just no-op the setter.
                ["sensing_setdragmode"] = new BlockDef(
                "sensing_setdragmode", BlockCategory.Sensing, BlockShape.Stack,
                "/* set drag mode: {0} — not applicable in desktop runtime */"),

                // -----------------------------------------------------------------
                //  OPERATORS
                // -----------------------------------------------------------------

                ["operator_add"] = new BlockDef("operator_add", BlockCategory.Operator, BlockShape.Reporter, "({0} + {1})"),
                ["operator_subtract"] = new BlockDef("operator_subtract", BlockCategory.Operator, BlockShape.Reporter, "({0} - {1})"),
                ["operator_multiply"] = new BlockDef("operator_multiply", BlockCategory.Operator, BlockShape.Reporter, "({0} * {1})"),
                ["operator_divide"] = new BlockDef("operator_divide", BlockCategory.Operator, BlockShape.Reporter, "({0} / {1})"),
                ["operator_mod"] = new BlockDef("operator_mod", BlockCategory.Operator, BlockShape.Reporter, "({0} % {1})"),

                ["operator_gt"] = new BlockDef("operator_gt", BlockCategory.Operator, BlockShape.Boolean, "({0} > {1})"),
                ["operator_lt"] = new BlockDef("operator_lt", BlockCategory.Operator, BlockShape.Boolean, "({0} < {1})"),
                ["operator_equals"] = new BlockDef("operator_equals", BlockCategory.Operator, BlockShape.Boolean,
                // Scratch equality is loose (string or numeric), we use a helper.
                "ScratchRuntime.Equals({0}, {1})"),

                ["operator_and"] = new BlockDef("operator_and", BlockCategory.Operator, BlockShape.Boolean, "({0} && {1})"),
                ["operator_or"] = new BlockDef("operator_or", BlockCategory.Operator, BlockShape.Boolean, "({0} || {1})"),
                ["operator_not"] = new BlockDef("operator_not", BlockCategory.Operator, BlockShape.Boolean, "(!({0}))"),

                ["operator_random"] = new BlockDef(
                "operator_random", BlockCategory.Operator, BlockShape.Reporter,
                "ScratchRuntime.PickRandom({0}, {1})"),

                ["operator_join"] = new BlockDef("operator_join", BlockCategory.Operator, BlockShape.Reporter, "({0}.ToString() + {1}.ToString())"),
                ["operator_letter_of"] = new BlockDef("operator_letter_of", BlockCategory.Operator, BlockShape.Reporter, "ScratchRuntime.LetterOf({0}, {1})"),
                ["operator_length"] = new BlockDef("operator_length", BlockCategory.Operator, BlockShape.Reporter, "{0}.ToString().Length"),
                ["operator_contains"] = new BlockDef("operator_contains", BlockCategory.Operator, BlockShape.Boolean, "{0}.ToString().ToLower().Contains({1}.ToString().ToLower())"),

                ["operator_mathop"] = new BlockDef(
                "operator_mathop", BlockCategory.Operator, BlockShape.Reporter,
                // OPERATOR field resolved by transpiler (abs, floor, ceiling, sqrt, etc.)
                "ScratchRuntime.MathOp(\"{0}\", {1})"),

                // -----------------------------------------------------------------
                //  DATA  (variables — lists are a future addition)
                // -----------------------------------------------------------------

                ["data_setvariableto"] = new BlockDef(
                "data_setvariableto", BlockCategory.Data, BlockShape.Stack,
                // Variable name resolved to the actual C# field name by transpiler.
                "{VAR} = ScratchRuntime.ToNumber({0})"),

                ["data_changevariableby"] = new BlockDef(
                "data_changevariableby", BlockCategory.Data, BlockShape.Stack,
                "{VAR} += ScratchRuntime.ToNumber({0})"),

                // Reporter that reads a variable value.
                ["data_variable"] = new BlockDef(
                "data_variable", BlockCategory.Data, BlockShape.Reporter,
                "{VAR}"),
            };

        // Convenience — returns null (not throws) for unknown opcodes so the
        // transpiler can emit a commented stub instead of crashing.
        public static BlockDef Get(string opcode)
        {
            return All.TryGetValue(opcode, out BlockDef def) ? def : null;
        }
    }
}