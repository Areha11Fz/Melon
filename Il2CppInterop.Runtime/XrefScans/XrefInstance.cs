using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Il2CppInterop.Runtime.XrefScans
{
    public readonly struct XrefInstance
    {
        public readonly XrefType Type;
        public readonly IntPtr Pointer;
        public readonly IntPtr FoundAt;

        public XrefInstance(XrefType type, IntPtr pointer, IntPtr foundAt)
        {
            Type = type;
            Pointer = pointer;
            FoundAt = foundAt;
        }

        internal XrefInstance RelativeToBase(long baseAddress)
        {
            return new XrefInstance(Type, (IntPtr)((long)Pointer - baseAddress), (IntPtr)((long)FoundAt - baseAddress));
        }

#if !MINI
        public Il2CppSystem.Object? ReadAsObject()
        {
            if (Type != XrefType.Global) throw new InvalidOperationException("Can't read non-global xref as object");

            var valueAtPointer = Marshal.ReadIntPtr(Pointer);
            if (valueAtPointer == IntPtr.Zero)
                return null;

            return new Il2CppSystem.Object(valueAtPointer);
        }
#endif

        public MethodBase TryResolve()
        {
            if (Type != XrefType.Method) throw new InvalidOperationException("Can't resolve non-method xrefs");

            return XrefScanMethodDb.TryResolvePointer(Pointer);
        }
    }
}