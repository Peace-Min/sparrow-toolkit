// Fixture harness for SparrowSyntaxFix. Every case below is grounded in REAL Sparrow output (see the task
// brief / docs): the nullcast cases are actual `<Type> x = null;` findings; the parens cases are actual
// &&/|| conditions the MISSING_PARENTHESIS_IN_EXPRESSION checker flagged. Each case wraps the snippet in a
// method so it parses as a real LocalDeclarationStatement / if-condition, and asserts the FULL transformed
// text (which also proves nothing else in the file moved). Exit code: 0 = all pass, 1 = any failure.

using System;
using System.Linq;
using SparrowSyntaxFix;

namespace SparrowSyntaxFix.FixtureTests
{
    internal static class Program
    {
        private static int _passed;
        private static int _failed;

        private static int Main()
        {
            Console.WriteLine("SparrowSyntaxFix fixture harness");
            Console.WriteLine("================================");

            NullVarPositives();
            NullVarNegatives();
            ObjectVarPositives();
            ObjectInitializerPositives();
            ObjectInitializerNegatives();
            ArrayVarPositives();
            ArrayVarNegatives();
            ForeachCastPositives();
            ForeachCastNegatives();
            ObviousVarPositives();
            ObviousVarNegatives();
            ForVarPositives();
            ForVarNegatives();
            ForHoistPositives();
            ForHoistNegatives();
            FieldSplitPositives();
            FieldSplitNegatives();
            EmptyStmtPositives();
            EmptyStmtNegatives();
            LocalConstPositives();
            ParensPositives();
            ParensNegatives();
            StringLiteralSafety();
            Idempotency();
            NewlinePreservation();

            Console.WriteLine("--------------------------------");
            Console.WriteLine("passed: " + _passed + "   failed: " + _failed);
            if (_failed > 0) { Console.WriteLine("RESULT: FAIL"); return 1; }
            Console.WriteLine("RESULT: PASS");
            return 0;
        }

        // --- wrappers so each snippet parses in a realistic context ---
        private static string Local(string stmt) =>
            "class C\n{\n    void M()\n    {\n        " + stmt + "\n    }\n}\n";

        private static string Cond(string expr) =>
            "class C\n{\n    void M()\n    {\n        if (" + expr + ")\n        {\n        }\n    }\n}\n";

        // ============================ Rule 1: nullcast ============================

        private static void NullVarPositives()
        {
            Console.WriteLine("[nullvar positive]");
            ExpectTransform("class type",
                Local("SampleComponent sampleComponent = null;"),
                Local("var sampleComponent = (SampleComponent)null;"),
                SyntaxRule.NullVar, expectNull: true, expectParens: false);

            ExpectTransform("sub-component type",
                Local("SampleSubComponent subComponent = null;"),
                Local("var subComponent = (SampleSubComponent)null;"),
                SyntaxRule.NullVar, true, false);

            ExpectTransform("generic List<PropData>",
                Local("List<PropData> lst = null;"),
                Local("var lst = (List<PropData>)null;"),
                SyntaxRule.NullVar, true, false);

            ExpectTransform("qualified name A.B.CThing",
                Local("A.B.CThing x = null;"),
                Local("var x = (A.B.CThing)null;"),
                SyntaxRule.NullVar, true, false);

            ExpectTransform("interface type (cast preserves it)",
                Local("IFoo c = null;"),
                Local("var c = (IFoo)null;"),
                SyntaxRule.NullVar, true, false);

            // Extra conservative cases (not in the brief's list but implied by "generics/qualified/arrays/nullable").
            ExpectTransform("nullable Foo?",
                Local("Foo? x = null;"),
                Local("var x = (Foo?)null;"),
                SyntaxRule.NullVar, true, false);

            ExpectTransform("array int[]",
                Local("int[] a = null;"),
                Local("var a = (int[])null;"),
                SyntaxRule.NullVar, true, false);

            ExpectTransform("no initializer reference-like type",
                Local("SampleComponent sampleComponent;"),
                Local("var sampleComponent = (SampleComponent)null;"),
                SyntaxRule.NullVar, true, false);
        }

        private static void NullVarNegatives()
        {
            Console.WriteLine("[nullvar negative — must stay byte-identical]");
            ExpectUnchanged("= new List<PropData>() under nullvar", Local("List<PropData> lst = new List<PropData>();"), SyntaxRule.NullVar);
            ExpectUnchanged("= default", Local("SampleComponent x = default;"), SyntaxRule.NullVar);
            ExpectUnchanged("= default(T)", Local("SampleComponent x = default(SampleComponent);"), SyntaxRule.NullVar);
            ExpectUnchanged("already var (invalid C#, must not crash/alter)", Local("var y = null;"), SyntaxRule.NullVar);
            ExpectUnchanged("multi-declarator", Local("SampleComponent a = null, b = null;"), SyntaxRule.NullVar);
            ExpectUnchanged("non-null initializer", Local("int n = GetCount();"), SyntaxRule.NullVar);
            ExpectUnchanged("predefined value type no initializer", Local("int n;"), SyntaxRule.NullVar);
            ExpectUnchanged("const declaration under nullvar", Local("const string s = null;"), SyntaxRule.NullVar);
        }

        private static void ObjectVarPositives()
        {
            Console.WriteLine("[objectvar positive]");
            ExpectTransform("safe same type",
                Local("Foo x = new Foo();"),
                Local("var x = new Foo();"),
                SyntaxRule.ObjectVarSafe, false, false, objectSafe: true);

            ExpectUnchanged("safe skips interface/base narrowing",
                Local("IFoo x = new Foo();"), SyntaxRule.ObjectVarSafe);

            ExpectTransform("review-needed narrowing",
                Local("IFoo x = new Foo();"),
                Local("var x = new Foo();"),
                SyntaxRule.ObjectVarNarrowing, false, false, objectNarrowing: true);
        }

        private static void ObjectInitializerPositives()
        {
            Console.WriteLine("[objectinitializer positive]");
            ExpectTransform("consecutive property assignments become initializer + var",
                Local("Foo x = new Foo();\n        x.A = 1;\n        x.B = 2;"),
                Local("var x = new Foo { A = 1, B = 2 };"),
                SyntaxRule.ObjectInitializer, false, false, objectInitializer: true);

            ExpectTransform("constructor arguments preserved",
                Local("Foo x = new Foo(seed);\n        x.A = 1;"),
                Local("var x = new Foo(seed) { A = 1 };"),
                SyntaxRule.ObjectInitializer, false, false, objectInitializer: true);
        }

        private static void ObjectInitializerNegatives()
        {
            Console.WriteLine("[objectinitializer negative]");
            ExpectUnchanged("middle use breaks consecutive region",
                Local("Foo x = new Foo();\n        Use(x);\n        x.A = 1;"),
                SyntaxRule.ObjectInitializer);
            ExpectUnchanged("RHS references the created variable",
                Local("Foo x = new Foo();\n        x.A = x.GetA();"),
                SyntaxRule.ObjectInitializer);
            ExpectUnchanged("indexer assignment skipped",
                Local("Foo x = new Foo();\n        x[0] = 1;"),
                SyntaxRule.ObjectInitializer);
            ExpectUnchanged("existing object initializer skipped",
                Local("Foo x = new Foo { A = 1 };\n        x.B = 2;"),
                SyntaxRule.ObjectInitializer);
            ExpectUnchanged("interface/base narrowing skipped by normal objectinitializer",
                Local("IFoo x = new Foo();\n        x.A = 1;"),
                SyntaxRule.ObjectInitializer);
            ExpectUnchanged("duplicate member assignment skipped",
                Local("Foo x = new Foo();\n        x.A = 1;\n        x.A = 2;"),
                SyntaxRule.ObjectInitializer);
            ExpectUnchanged("comments inside candidate skipped",
                Local("Foo x = new Foo();\n        // keep this comment\n        x.A = 1;"),
                SyntaxRule.ObjectInitializer);
            ExpectUnchanged("invocation RHS skipped because it may indirectly reference the local",
                Local("Foo x = new Foo();\n        x.A = GetX();"),
                SyntaxRule.ObjectInitializer);
        }

        private static void ArrayVarPositives()
        {
            Console.WriteLine("[arrayvar positive]");
            ExpectTransform("same array type",
                Local("int[] values = new int[] { 1, 2, 3 };"),
                Local("int[] values = { 1, 2, 3 };"),
                SyntaxRule.ArrayVarSafe, false, false, arraySafe: true);

            ExpectTransform("same object array type preserves runtime object[]",
                Local("object[] values = new object[] { \"A\" };"),
                Local("object[] values = { \"A\" };"),
                SyntaxRule.ArrayVarSafe, false, false, arraySafe: true);

            ExpectTransform("review-needed array narrowing",
                Local("object[] values = new string[] { \"A\", \"B\" };"),
                Local("var values = new[] { \"A\", \"B\" };"),
                SyntaxRule.ArrayVarNarrowing, false, false, arrayNarrowing: true);
        }

        private static void ArrayVarNegatives()
        {
            Console.WriteLine("[arrayvar negative]");
            ExpectUnchanged("safe skips different static/runtime array type",
                Local("object[] values = new string[] { \"A\", \"B\" };"),
                SyntaxRule.ArrayVarSafe);
            ExpectUnchanged("assignment statement skipped",
                Local("int[] values;\n        values = new int[] { 1, 2, 3 };"),
                SyntaxRule.ArrayVarSafe);
            ExpectUnchanged("already implicit array creation",
                Local("var values = new[] { 1, 2, 3 };"),
                SyntaxRule.ArrayVarSafe);
            ExpectUnchanged("empty array skipped because new[] cannot infer and safe rewrite may be checker-specific",
                Local("int[] values = new int[] { };"),
                SyntaxRule.ArrayVarSafe);
            ExpectUnchanged("explicit size skipped",
                Local("int[] values = new int[3] { 1, 2, 3 };"),
                SyntaxRule.ArrayVarSafe);
            ExpectUnchanged("jagged array skipped",
                Local("int[][] values = new int[][] { new int[] { 1 } };"),
                SyntaxRule.ArrayVarSafe);
            ExpectUnchanged("narrowing skips null because new[] cannot infer",
                Local("object[] values = new string[] { null };"),
                SyntaxRule.ArrayVarNarrowing);
            ExpectUnchanged("narrowing skips default because new[] cannot infer",
                Local("object[] values = new string[] { default(string) };"),
                SyntaxRule.ArrayVarNarrowing);
            ExpectUnchanged("narrowing skips method group target typing",
                Local("Delegate[] handlers = new Action[] { Target };"),
                SyntaxRule.ArrayVarNarrowing);
            ExpectUnchanged("narrowing skips parenthesized method group target typing",
                Local("Delegate[] handlers = new Action[] { (Target) };"),
                SyntaxRule.ArrayVarNarrowing);
        }

        private static void ForeachCastPositives()
        {
            Console.WriteLine("[foreachcast positive]");
            ExpectTransform("XmlNode foreach gets Cast<T>",
                "class C\n{\n    void M()\n    {\n        XmlNodeList clsNodes = GetNodes();\n        foreach (XmlNode node in clsNodes)\n        {\n            _ = node.Name;\n        }\n    }\n}\n",
                "class C\n{\n    void M()\n    {\n        XmlNodeList clsNodes = GetNodes();\n        foreach (var node in System.Linq.Enumerable.Cast<XmlNode>(clsNodes))\n        {\n            _ = node.Name;\n        }\n    }\n}\n",
                SyntaxRule.ForeachCast, false, false, foreachCast: true);

            // Real MyApp pattern: collection is a member access (x.ChildNodes), not a locally-declared
            // XmlNodeList. The original foreach already casts each element to XmlNode, so Cast<XmlNode>(expr)
            // is semantics-equivalent for any collection expression.
            ExpectTransform("member-access / bare collection also converts",
                "class C\n{\n    void M(System.Xml.XmlNode root)\n    {\n        foreach (XmlNode node in root.ChildNodes)\n        {\n            _ = node.Name;\n        }\n    }\n}\n",
                "class C\n{\n    void M(System.Xml.XmlNode root)\n    {\n        foreach (var node in System.Linq.Enumerable.Cast<XmlNode>(root.ChildNodes))\n        {\n            _ = node.Name;\n        }\n    }\n}\n",
                SyntaxRule.ForeachCast, false, false, foreachCast: true);

            // Generalized beyond XmlNode: ANY explicit element type over a non-generic collection converts.
            // DataColumn over DataTable.Columns (DataColumnCollection : non-generic IEnumerable).
            ExpectTransform("DataColumn over member-access collection converts",
                "class C\n{\n    void M(System.Data.DataTable srcTable)\n    {\n        foreach (System.Data.DataColumn c in srcTable.Columns)\n        {\n            _ = c.ColumnName;\n        }\n    }\n}\n",
                "class C\n{\n    void M(System.Data.DataTable srcTable)\n    {\n        foreach (var c in System.Linq.Enumerable.Cast<System.Data.DataColumn>(srcTable.Columns))\n        {\n            _ = c.ColumnName;\n        }\n    }\n}\n",
                SyntaxRule.ForeachCast, false, false, foreachCast: true);

            // TabItem over ItemCollection (WPF, non-generic IEnumerable).
            ExpectTransform("TabItem over WPF ItemCollection converts",
                "class C\n{\n    void M(System.Windows.Controls.TabControl tab)\n    {\n        foreach (System.Windows.Controls.TabItem obj in tab.Items)\n        {\n            _ = obj;\n        }\n    }\n}\n",
                "class C\n{\n    void M(System.Windows.Controls.TabControl tab)\n    {\n        foreach (var obj in System.Linq.Enumerable.Cast<System.Windows.Controls.TabItem>(tab.Items))\n        {\n            _ = obj;\n        }\n    }\n}\n",
                SyntaxRule.ForeachCast, false, false, foreachCast: true);

            // Generic collection member-access: List<TreeNodeData> over delObject.Items.
            ExpectTransform("explicit type over generic List<T> member-access converts",
                "class C\n{\n    void M(H delObject)\n    {\n        foreach (TreeNodeData x in delObject.Items)\n        {\n            _ = x;\n        }\n    }\n}\n",
                "class C\n{\n    void M(H delObject)\n    {\n        foreach (var x in System.Linq.Enumerable.Cast<TreeNodeData>(delObject.Items))\n        {\n            _ = x;\n        }\n    }\n}\n",
                SyntaxRule.ForeachCast, false, false, foreachCast: true);

            // Formerly a NEGATIVE ("pattern enumerator skipped"): an explicit type over a non-XmlNode
            // collection now CONVERTS (the XmlNode-only restriction was dropped). Cast<Foo>(xs) is
            // semantics-equivalent because the original foreach already cast each element to Foo.
            ExpectTransform("explicit type over arbitrary collection now converts",
                "class C\n{\n    void M()\n    {\n        var xs = GetItems();\n        foreach (Foo node in xs)\n        {\n            _ = node;\n        }\n    }\n}\n",
                "class C\n{\n    void M()\n    {\n        var xs = GetItems();\n        foreach (var node in System.Linq.Enumerable.Cast<Foo>(xs))\n        {\n            _ = node;\n        }\n    }\n}\n",
                SyntaxRule.ForeachCast, false, false, foreachCast: true);
        }

        private static void ForeachCastNegatives()
        {
            Console.WriteLine("[foreachcast negative]");
            // Already `var` -> nothing to widen.
            ExpectUnchanged("already-var loop skipped",
                "class C\n{\n    void M()\n    {\n        foreach (var node in xs)\n        {\n            _ = node;\n        }\n    }\n}\n",
                SyntaxRule.ForeachCast);
            // Already Cast<T>(...) -> idempotent: a second pass must be a no-op.
            ExpectUnchanged("already-Cast<T> source skipped (idempotent)",
                "class C\n{\n    void M()\n    {\n        foreach (XmlNode node in System.Linq.Enumerable.Cast<XmlNode>(clsNodes))\n        {\n            _ = node.Name;\n        }\n    }\n}\n",
                SyntaxRule.ForeachCast);
            // Already OfType<T>(...) -> idempotent no-op.
            ExpectUnchanged("already-OfType<T> source skipped (idempotent)",
                "class C\n{\n    void M()\n    {\n        foreach (XmlNode node in System.Linq.Enumerable.OfType<XmlNode>(clsNodes))\n        {\n            _ = node.Name;\n        }\n    }\n}\n",
                SyntaxRule.ForeachCast);

            // VALUE-TYPE GUARD (runtime-safety): the original foreach does an IMPLICIT NUMERIC conversion, not a
            // cast, so Enumerable.Cast<T> would unbox to the wrong runtime type and throw InvalidCastException at
            // the first iteration. These MUST stay byte-identical.
            //   `foreach (double d in intList)` — int→double widening; Cast<double>(intList) throws at runtime.
            ExpectUnchanged("value-type guard: double over int collection skipped",
                "class C\n{\n    void M()\n    {\n        var intList = GetInts();\n        foreach (double d in intList)\n        {\n            _ = d;\n        }\n    }\n}\n",
                SyntaxRule.ForeachCast);
            //   `foreach (int i in intList)` — Cast<int> WOULD be safe here, but the guard conservatively skips
            //   ALL numeric value-type element types (syntax cannot tell a safe case from an unsafe one).
            ExpectUnchanged("value-type guard: int over int collection skipped (conservative)",
                "class C\n{\n    void M()\n    {\n        var intList = GetInts();\n        foreach (int i in intList)\n        {\n            _ = i;\n        }\n    }\n}\n",
                SyntaxRule.ForeachCast);
            //   `foreach (long x in arr)` — int[]→long widening; Cast<long> throws.
            ExpectUnchanged("value-type guard: long over array skipped",
                "class C\n{\n    void M(int[] arr)\n    {\n        foreach (long x in arr)\n        {\n            _ = x;\n        }\n    }\n}\n",
                SyntaxRule.ForeachCast);
            //   `foreach (int? n in list)` — nullable element type; unbox-to-nullable kept out conservatively.
            ExpectUnchanged("value-type guard: nullable int? skipped",
                "class C\n{\n    void M()\n    {\n        var list = GetInts();\n        foreach (int? n in list)\n        {\n            _ = n;\n        }\n    }\n}\n",
                SyntaxRule.ForeachCast);
            //   `System.`-qualified well-known value type name is guarded too.
            ExpectUnchanged("value-type guard: System.Double name skipped",
                "class C\n{\n    void M()\n    {\n        var list = GetInts();\n        foreach (System.Double d in list)\n        {\n            _ = d;\n        }\n    }\n}\n",
                SyntaxRule.ForeachCast);
        }

        private static void ObviousVarPositives()
        {
            Console.WriteLine("[obviousvar positive]");
            ExpectTransform("string literal",
                Local("string s = \"A\";"),
                Local("var s = \"A\";"),
                SyntaxRule.ObviousVar, false, false, obvious: true);

            ExpectTransform("double numeric literal keeps type with cast",
                Local("double markerH = 20;"),
                Local("var markerH = (double)20;"),
                SyntaxRule.ObviousVar, false, false, obvious: true);

            ExpectTransform("nullable numeric literal keeps type with cast",
                Local("int? pageSize = 0;"),
                Local("var pageSize = (int?)0;"),
                SyntaxRule.ObviousVar, false, false, obvious: true);

        }

        private static void ObviousVarNegatives()
        {
            Console.WriteLine("[obviousvar negative]");
            ExpectUnchanged("object string literal narrowing skipped",
                Local("object value = \"x\";"),
                SyntaxRule.ObviousVar);
            ExpectUnchanged("object char literal narrowing skipped",
                Local("object value = 'x';"),
                SyntaxRule.ObviousVar);
            ExpectUnchanged("object bool literal narrowing skipped",
                Local("object value = true;"),
                SyntaxRule.ObviousVar);
            ExpectUnchanged("object cast narrowing skipped",
                Local("object value = (string)text;"),
                SyntaxRule.ObviousVar);
            ExpectUnchanged("long Convert.ToInt32 narrowing skipped",
                Local("long value = Convert.ToInt32(text);"),
                SyntaxRule.ObviousVar);
            ExpectUnchanged("unqualified Convert skipped because it may be shadowed",
                Local("int value = Convert.ToInt32(text);"),
                SyntaxRule.ObviousVar);
            ExpectUnchanged("System.Convert skipped because it may be shadowed",
                Local("int value = System.Convert.ToInt32(text);"),
                SyntaxRule.ObviousVar);
            ExpectUnchanged("global System.Convert skipped because syntax-only cannot prove symbol identity",
                Local("int value = global::System.Convert.ToInt32(text);"),
                SyntaxRule.ObviousVar);
        }

        // ============================ Rule: forvar (opt-in) ============================

        private static void ForVarPositives()
        {
            Console.WriteLine("[forvar positive]");
            // Exact brief fixture (QueryService.cs:413): single-declarator obvious int init -> var.
            ExpectTransform("for-init int literal -> var",
                "class C\n{\n    void M(int count)\n    {\n        for (int i = 0; i < count; i++)\n        {\n        }\n    }\n}\n",
                "class C\n{\n    void M(int count)\n    {\n        for (var i = 0; i < count; i++)\n        {\n        }\n    }\n}\n",
                SyntaxRule.ForVar, false, false, forVar: true);

            // Non-int numeric keeps its element type via a cast (mirrors obviousvar).
            ExpectTransform("for-init double literal keeps type with cast",
                "class C\n{\n    void M(int n)\n    {\n        for (double d = 20; d < n; d++)\n        {\n        }\n    }\n}\n",
                "class C\n{\n    void M(int n)\n    {\n        for (var d = (double)20; d < n; d++)\n        {\n        }\n    }\n}\n",
                SyntaxRule.ForVar, false, false, forVar: true);
        }

        private static void ForVarNegatives()
        {
            Console.WriteLine("[forvar negative — must stay byte-identical]");
            // Multi-declarator: `for (int i = 0, count = ...)` -> `var i = 0, count = ...` is CS0819. NEVER touch.
            ExpectUnchanged("multi-declarator for-init",
                "class C\n{\n    void M(System.Collections.Generic.List<int> q)\n    {\n        for (int i = 0, count = q.Count; i < count; i++)\n        {\n        }\n    }\n}\n",
                SyntaxRule.ForVar);
            // Method-call initializer: narrowing risk -> skip (RewriteInitializer returns null).
            ExpectUnchanged("method-call for-init",
                "class C\n{\n    int G() { return 0; }\n    void M(int n)\n    {\n        for (int j = G(); j < n; j++)\n        {\n        }\n    }\n}\n",
                SyntaxRule.ForVar);
            // Already var: idempotent no-op.
            ExpectUnchanged("already-var for-init",
                "class C\n{\n    void M(int n)\n    {\n        for (var k = 0; k < n; k++)\n        {\n        }\n    }\n}\n",
                SyntaxRule.ForVar);
        }

        // ============================ Rule: forhoist (opt-in) ============================

        private static void ForHoistPositives()
        {
            Console.WriteLine("[forhoist positive]");
            // Exact brief fixture (WorkQueue.cs:181 etc.): the multi-declarator for-init that LOOP_VARIABLE +
            // USE_ONE_DECLARATION + OBVIOUS_VARIABLE_TYPE flag together and that cannot be fixed in place. The
            // non-loop declarator (count) is hoisted to its own `var` local; the loop var stays as a single
            // `var i = 0` declarator.
            ExpectTransform("multi-declarator for-init hoists non-loop declarator",
                "class C\n{\n    void M(System.Collections.Generic.Queue<int> queue)\n    {\n        for (int i = 0, count = queue.Count; i < count; i++)\n        {\n        }\n    }\n}\n",
                "class C\n{\n    void M(System.Collections.Generic.Queue<int> queue)\n    {\n        var count = queue.Count;\n        for (var i = 0; i < count; i++)\n        {\n        }\n    }\n}\n",
                SyntaxRule.ForHoist, false, false, forHoist: true);
        }

        private static void ForHoistNegatives()
        {
            Console.WriteLine("[forhoist negative — must stay byte-identical]");
            // Single declarator: nothing to hoist (this is forvar's job, not forhoist's).
            ExpectUnchanged("single-declarator for-init untouched",
                "class C\n{\n    void M(int count)\n    {\n        for (int i = 0; i < count; i++)\n        {\n        }\n    }\n}\n",
                SyntaxRule.ForHoist);
            // Dependency: the hoisted declarator's initializer references the loop variable -> moving it before
            // the for would read a not-yet-declared name / change evaluation order. Skip.
            ExpectUnchanged("dependency: hoisted init references loop var",
                "class C\n{\n    void M(int[] a)\n    {\n        for (int i = 0, sum = a[i]; i < a.Length; i++)\n        {\n        }\n    }\n}\n",
                SyntaxRule.ForHoist);
            // Name-collision: `count` is already a local in the enclosing method -> hoisting would shadow it. Skip.
            ExpectUnchanged("name-collision: hoisted name already a local",
                "class C\n{\n    void M(System.Collections.Generic.Queue<int> queue)\n    {\n        int count = 5;\n        for (int i = 0, count = queue.Count; i < count; i++)\n        {\n        }\n    }\n}\n",
                SyntaxRule.ForHoist);
            // Non-obvious (null) initializer on the hoisted declarator keeps the EXPLICIT type (var cannot infer
            // from null). The loop var still becomes var.
            ExpectTransform("non-obvious (null) hoisted declarator keeps explicit type",
                "class C\n{\n    void M(int n)\n    {\n        for (int i = 0, node = default; i < n; i++)\n        {\n        }\n    }\n}\n",
                "class C\n{\n    void M(int n)\n    {\n        int node = default;\n        for (var i = 0; i < n; i++)\n        {\n        }\n    }\n}\n",
                SyntaxRule.ForHoist, false, false, forHoist: true);
        }

        // ============================ Rule: fieldsplit (opt-in) ============================

        private static void FieldSplitPositives()
        {
            Console.WriteLine("[fieldsplit positive]");
            // Exact brief fixture (ChartViewModel.cs:183): 4 declarators -> 4 fields, same indent.
            ExpectTransform("multi-declarator field split one per line",
                "class C\n{\n    private double _rawXMin, _rawXMax, _rawYMin, _rawYMax;\n}\n",
                "class C\n{\n    private double _rawXMin;\n    private double _rawXMax;\n    private double _rawYMin;\n    private double _rawYMax;\n}\n",
                SyntaxRule.FieldSplit, false, false, fieldSplit: true);

            // Initializers are attributed to their own declarator.
            ExpectTransform("field split preserves each initializer",
                "class C\n{\n    public int a = 1, b = 2;\n}\n",
                "class C\n{\n    public int a = 1;\n    public int b = 2;\n}\n",
                SyntaxRule.FieldSplit, false, false, fieldSplit: true);

            // A leading comment stays on the FIRST split line, not duplicated.
            ExpectTransform("field split keeps leading comment on first line",
                "class C\n{\n    // keep me\n    private int x, y;\n}\n",
                "class C\n{\n    // keep me\n    private int x;\n    private int y;\n}\n",
                SyntaxRule.FieldSplit, false, false, fieldSplit: true);
        }

        private static void FieldSplitNegatives()
        {
            Console.WriteLine("[fieldsplit negative — must stay byte-identical]");
            // Local variable declaration: out of scope (fields only).
            ExpectUnchanged("local multi-declarator not a field",
                "class C\n{\n    void M()\n    {\n        int x, y;\n    }\n}\n",
                SyntaxRule.FieldSplit);
            // Single declarator: nothing to split.
            ExpectUnchanged("single-declarator field",
                "class C\n{\n    private int only;\n}\n",
                SyntaxRule.FieldSplit);
            // const field: brief says skip.
            ExpectUnchanged("const multi-declarator field",
                "class C\n{\n    private const int A = 1, B = 2;\n}\n",
                SyntaxRule.FieldSplit);
        }

        // ============================ Rule: emptystmt (opt-in) ============================

        private static void EmptyStmtPositives()
        {
            Console.WriteLine("[emptystmt positive]");
            // Exact brief fixture (MainView.xaml.cs:1017): `stmt; ;` -> `stmt;`.
            ExpectTransform("double-semicolon collapses to one",
                "class C\n{\n    event System.EventHandler E;\n    void H(object s, System.EventArgs e) { }\n    void M()\n    {\n        E += H; ;\n    }\n}\n",
                "class C\n{\n    event System.EventHandler E;\n    void H(object s, System.EventArgs e) { }\n    void M()\n    {\n        E += H;\n    }\n}\n",
                SyntaxRule.EmptyStmt, false, false, emptyStmt: true);
        }

        private static void EmptyStmtNegatives()
        {
            Console.WriteLine("[emptystmt negative — must stay byte-identical]");
            // Empty for-body clause is semantically meaningful (Parent is the ForStatement, not a Block).
            ExpectUnchanged("for(;;) empty body kept",
                "class C\n{\n    void M()\n    {\n        for (;;) ;\n    }\n}\n",
                SyntaxRule.EmptyStmt);
            // while empty body kept.
            ExpectUnchanged("while empty body kept",
                "class C\n{\n    void M(bool c)\n    {\n        while (Poll(c)) ;\n    }\n    bool Poll(bool c) { return false; }\n}\n",
                SyntaxRule.EmptyStmt);
            // Labeled empty statement kept (label needs a statement).
            ExpectUnchanged("labeled empty statement kept",
                "class C\n{\n    void M()\n    {\n        goto done;\n    done: ;\n    }\n}\n",
                SyntaxRule.EmptyStmt);
        }

        private static void LocalConstPositives()
        {
            Console.WriteLine("[localconst positive]");
            ExpectTransform("const string demoted",
                Local("const string s = \"A\";"),
                Local("var s = \"A\";"),
                SyntaxRule.LocalConst, false, false, localConst: true);

            ExpectTransform("const double demoted with cast",
                Local("const double d = 20;"),
                Local("var d = (double)20;"),
                SyntaxRule.LocalConst, false, false, localConst: true);
        }

        // ============================ Rule 2: parens ============================

        private static void ParensPositives()
        {
            Console.WriteLine("[parens positive]");
            ExpectTransform("relational group",
                Cond("nIndex > 0 && nIndex <= nCount - 1"),
                Cond("(nIndex > 0) && (nIndex <= nCount - 1)"),
                SyntaxRule.Parens, expectNull: false, expectParens: true);

            ExpectTransform("null-checks with &&",
                Cond("sampleComponent != null && dataTypeInfo != null"),
                Cond("(sampleComponent != null) && (dataTypeInfo != null)"),
                SyntaxRule.Parens, false, true);

            ExpectTransform("equality with ||",
                Cond("nodeTag == NodeTag.Msg || nodeTag == NodeTag.MsgPlayer"),
                Cond("(nodeTag == NodeTag.Msg) || (nodeTag == NodeTag.MsgPlayer)"),
                SyntaxRule.Parens, false, true);

            ExpectTransform("mixed &&/|| wraps the && group and every leaf",
                Cond("a || b && c"),
                Cond("(a) || ((b) && (c))"),
                SyntaxRule.Parens, false, true);

            // Sparrow wants EVERY operand wrapped, including atoms — `(a) || b` stays flagged, `(a) || (b)` clears.
            ExpectTransform("atom operands (identifiers) are wrapped too",
                Local("bool ok = a && b;"),
                Local("bool ok = (a) && (b);"),
                SyntaxRule.Parens, false, true);

            ExpectTransform("invocation operands are wrapped",
                Cond("finfile.Name.Equals(\"x\") || finfile.Name.Equals(\"y\")"),
                Cond("(finfile.Name.Equals(\"x\")) || (finfile.Name.Equals(\"y\"))"),
                SyntaxRule.Parens, false, true);

            // A half-done expression from an earlier/older pass is completed, not left asymmetric.
            ExpectTransform("completes a partially-parenthesized operand",
                Cond("(a > 0) || flag"),
                Cond("(a > 0) || (flag)"),
                SyntaxRule.Parens, false, true);
        }

        private static void ParensNegatives()
        {
            Console.WriteLine("[parens negative — must stay byte-identical]");
            // Both operands already parenthesized -> idempotent, no change (and the inner `a > 0` / `b < 1`
            // are operands of a comparison, not of &&/||, so they are not wrapped either).
            ExpectUnchanged("already parenthesized (idempotent)",
                Cond("(a > 0) && (b < 1)"), SyntaxRule.All);
        }

        private static void StringLiteralSafety()
        {
            Console.WriteLine("[string/char literal safety]");
            // A string literal containing && / = null must be untouched (whole file byte-identical).
            ExpectUnchanged("string literal with && and = null inside",
                Local("string s = \"a && b = null\";"), SyntaxRule.Parens);

            // Real code around a string with && gets wrapped; the string's inner && is left alone.
            ExpectTransform("wrap real operands, not the && inside the string",
                Cond("a > 0 && msg == \"x && y\""),
                Cond("(a > 0) && (msg == \"x && y\")"),
                SyntaxRule.Parens, false, true);
        }

        // ============================ idempotency + newlines ============================

        private static void Idempotency()
        {
            Console.WriteLine("[idempotency — second run makes zero changes]");
            string[] inputs =
            {
                Local("SampleComponent sampleComponent = null;"),
                Cond("nIndex > 0 && nIndex <= nCount - 1"),
                Cond("a || b && c"),
                Cond("a > 0 && msg == \"x && y\""),
            };
            foreach (string input in inputs)
            {
                RewriteResult first = RewriteEngine.Rewrite(input, SyntaxRule.All);
                RewriteResult second = RewriteEngine.Rewrite(first.NewText, SyntaxRule.All);
                bool ok = first.Changed
                          && !second.Changed
                          && second.NewText == first.NewText
                          && AllZero(second);
                Report("idempotent: " + OneLine(input), ok, ok ? "" : "second run changed the text or reported edits");
            }
        }

        private static void NewlinePreservation()
        {
            Console.WriteLine("[CRLF preservation]");
            string crlf =
                "class C\r\n{\r\n    void M()\r\n    {\r\n" +
                "        CFoo x = null;\r\n" +
                "        if (a > 0 && b < 1)\r\n" +
                "        {\r\n        }\r\n    }\r\n}\r\n";

            RewriteResult r = RewriteEngine.Rewrite(crlf, SyntaxRule.All);
            bool ok = r.Changed
                      && r.NewText.Contains("var x = (CFoo)null;")
                      && r.NewText.Contains("(a > 0) && (b < 1)")
                      && r.NewText.Contains("\r\n")
                      && !HasLoneLf(r.NewText);
            Report("CRLF preserved through nullcast + parens edits", ok, ok ? "" : Escape(r.NewText));
        }

        private static bool HasLoneLf(string s)
        {
            for (int i = 0; i < s.Length; i++)
                if (s[i] == '\n' && (i == 0 || s[i - 1] != '\r')) return true;
            return false;
        }

        // ============================ assertion helpers ============================

        private static void ExpectTransform(string label, string input, string expected, SyntaxRule rules,
                                            bool expectNull, bool expectParens,
                                            bool objectSafe = false, bool foreachCast = false,
                                            bool obvious = false, bool objectNarrowing = false,
                                            bool localConst = false,
                                            bool objectInitializer = false,
                                            bool arraySafe = false,
                                            bool arrayNarrowing = false,
                                            bool forVar = false,
                                            bool fieldSplit = false,
                                            bool emptyStmt = false,
                                            bool forHoist = false)
        {
            RewriteResult r = RewriteEngine.Rewrite(input, rules);
            bool textOk = r.NewText == expected;
            bool countOk = (r.NullVarEdits > 0) == expectNull
                           && (r.ParensEdits > 0) == expectParens
                           && (r.ObjectVarSafeEdits > 0) == objectSafe
                           && (r.ForeachCastEdits > 0) == foreachCast
                           && (r.ObviousVarEdits > 0) == obvious
                           && (r.ObjectVarNarrowingEdits > 0) == objectNarrowing
                           && (r.LocalConstEdits > 0) == localConst
                           && (r.ObjectInitializerEdits > 0) == objectInitializer
                           && (r.ArrayVarSafeEdits > 0) == arraySafe
                           && (r.ArrayVarNarrowingEdits > 0) == arrayNarrowing
                           && (r.ForVarEdits > 0) == forVar
                           && (r.FieldSplitEdits > 0) == fieldSplit
                           && (r.EmptyStmtEdits > 0) == emptyStmt
                           && (r.ForHoistEdits > 0) == forHoist
                           && r.Changed;
            Report(label, textOk && countOk, textOk ? CountsText(r) : Diff(expected, r.NewText));
        }

        private static void ExpectUnchanged(string label, string input, SyntaxRule rules)
        {
            RewriteResult r = RewriteEngine.Rewrite(input, rules);
            bool ok = !r.Changed && r.NewText == input && AllZero(r);
            Report(label, ok, ok ? "" : "expected byte-identical; got: " + Escape(r.NewText));
        }

        private static bool AllZero(RewriteResult r)
        {
            foreach (var kv in r.Counts)
                if (kv.Value != 0) return false;
            return true;
        }

        private static string CountsText(RewriteResult r) =>
            "counts: " + string.Join(" ", r.Counts.Select(kv => kv.Key + "=" + kv.Value));

        private static void Report(string label, bool ok, string detail)
        {
            if (ok)
            {
                _passed++;
                Console.WriteLine("  [ok]   " + label);
            }
            else
            {
                _failed++;
                Console.WriteLine("  [FAIL] " + label);
                if (detail.Length > 0) Console.WriteLine("         " + detail.Replace("\n", "\n         "));
            }
        }

        private static string Diff(string expected, string actual) =>
            "expected: " + Escape(expected) + "\nactual:   " + Escape(actual);

        private static string Escape(string s) => s.Replace("\r", "\\r").Replace("\n", "\\n");

        private static string OneLine(string wrapped)
        {
            // Pull the inner statement/condition out of the wrapper for a compact idempotency label.
            string flat = wrapped.Replace("\n", " ").Replace("\r", " ");
            return flat.Length <= 60 ? flat.Trim() : flat.Substring(0, 60).Trim() + "...";
        }
    }
}
