using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DmGodot.Runtime;

public sealed partial class DmRuntime {
    private sealed record Coefficients(float A, float B, float C, float D, float E, float F) {
        internal static readonly Coefficients Identity = new(1, 0, 0, 0, 1, 0);
    }

    private sealed record MatrixLayout(TypeDefaults Prototype, long CopyWork);
    private MatrixLayout? _matrixLayout;
    // Sparse immutable appearance state. Neither a mutable matrix nor its variable
    // dictionary enters an atom's shared defaults or survives through a getter.
    private readonly ConditionalWeakTable<DmObject, Coefficients> _atomTransforms = new();

    private static bool IsMatrixCallShape(byte mode, int count) =>
        (mode == 0 && count == 0) || (mode == 1 && count == 6);

    private void MatrixWork(long count) {
        _startupCancellation.ThrowIfCancellationRequested();
        if (count > _budget) throw new CompatibilityException("Matrix work budget exhausted.");
        _budget -= (int)count;
    }

    private static float FiniteCoefficient(DmValue value) =>
        value.Value is float n && float.IsFinite(n) ? n
            : throw new CompatibilityException("Matrix coefficients require finite numbers.");

    private MatrixLayout GetMatrixLayout() {
        if (_matrixLayout is not null) return _matrixLayout;
        var id = Project.MatrixTypeId;
        if (id < 0 || Data.Types[id].Parent is not int datum || Data.Types[datum].Path != "/datum" ||
            Data.Types[datum].Parent != 0 || Data.Types[0].Path != "/" || Data.Types[0].Parent is not null)
            throw new CompatibilityException("Native /matrix requires the pinned /matrix, /datum and root hierarchy.");

        TypeDefaults? prototype = null;
        foreach (var typeId in new[] { 0, datum, id }) {
            MatrixWork(1);
            var type = Data.Types[typeId];
            if (type.InitProc is not null)
                throw new CompatibilityException("Executable /matrix type initialization is not implemented.");
            if (type.ConstVariables is not null) {
                foreach (var name in type.ConstVariables) {
                    MatrixWork(1);
                    if (name is "a" or "b" or "c" or "d" or "e" or "f")
                        throw new CompatibilityException("Const native matrix coefficients are not implemented.");
                }
            }
            if (!_typeDefaults.TryGetValue(typeId, out var cached)) {
                // GetTypeDefaults copies and hashes inherited and declared slots;
                // bound those copies and decoding before any new prototype is built.
                if (prototype is not null)
                    foreach (var name in prototype.Variables.Keys) MatrixWork(1L + name.Length);
                if (type.Variables is not null)
                    foreach (var (name, raw) in type.Variables)
                        MatrixWork(1L + name.Length + JsonMarshal.GetRawUtf8Value(raw).Length);
                cached = GetTypeDefaults(typeId, initializeLater: false);
            }
            prototype = cached;
        }
        // Preserve inherited scalar user fields. Unknown native coefficient schemas or
        // an absent declaration must not silently turn into a fabricated matrix type.
        foreach (var name in new[] { "a", "b", "c", "d", "e", "f" }) {
            MatrixWork(1);
            if (Data.Types[id].Variables?.ContainsKey(name) != true ||
                prototype!.Variables[name].Value is not float n || !float.IsFinite(n))
                throw new CompatibilityException("Custom native matrix coefficient defaults are not implemented.");
        }

        // Every native return creates a fresh instance and materializes the ordinary
        // datum dictionary. Include metadata keys and all six fixed writes/boxings.
        long copyWork = 48;
        foreach (var name in prototype!.Variables.Keys) {
            MatrixWork(1);
            copyWork += 1L + name.Length;
        }
        return _matrixLayout = new(prototype, copyWork);
    }

    private DmObject NewMatrix(DmValue[] args) {
        MatrixWork(7);
        var coefficients = args.Length == 0 ? Coefficients.Identity :
            new(FiniteCoefficient(args[0]), FiniteCoefficient(args[1]), FiniteCoefficient(args[2]),
                FiniteCoefficient(args[3]), FiniteCoefficient(args[4]), FiniteCoefficient(args[5]));
        return CopyMatrix(coefficients);
    }

    private DmObject CopyMatrix(Coefficients coefficients) {
        var layout = GetMatrixLayout();
        MatrixWork(layout.CopyWork);
        var prototype = layout.Prototype;
        var matrix = DmObject.CreateNativeMatrix(Project.MatrixTypeId, prototype.Variables, prototype.Type, prototype.ParentType);
        matrix.SetVariable("a", coefficients.A); matrix.SetVariable("b", coefficients.B);
        matrix.SetVariable("c", coefficients.C); matrix.SetVariable("d", coefficients.D);
        matrix.SetVariable("e", coefficients.E); matrix.SetVariable("f", coefficients.F);
        return matrix;
    }

    private bool HasNativeTransform(DmObject obj) {
        if ((uint)obj.TypeId >= (uint)Data.Types.Length) return false;
        for (int? id = obj.TypeId; id is int current; id = Data.Types[current].Parent) {
            MatrixWork(1);
            if (Data.Types[current].Path != "/atom") continue;
            if (!obj.TryGetVariable("transform", out var raw) || !raw.IsNull)
                throw new CompatibilityException("Non-default atom.transform prototype or raw override is not implemented.");
            return true;
        }
        return false;
    }

    private DmObject ReadTransform(DmObject atom) {
        MatrixWork(1);
        return CopyMatrix(_atomTransforms.TryGetValue(atom, out var current) ? current : Coefficients.Identity);
    }

    private void WriteTransform(DmObject atom, DmValue value) {
        if (value.Value is not DmObject { IsNativeMatrix: true } matrix || matrix.TypeId != Project.MatrixTypeId)
            throw new CompatibilityException("Only native matrix assignment to atom.transform is implemented.");
        MatrixWork(16); // Six reads, immutable six-float snapshot and sparse storage.
        float FieldNumber(string name) => FiniteCoefficient(matrix.GetVariableOrDefault(name));
        var snapshot = new Coefficients(FieldNumber("a"), FieldNumber("b"), FieldNumber("c"),
            FieldNumber("d"), FieldNumber("e"), FieldNumber("f"));
        _atomTransforms.AddOrUpdate(atom, snapshot);
    }
}
