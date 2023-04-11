using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Reflection;
using Il2CppInterop.Common;
using Microsoft.Extensions.Logging;
using Il2CppInterop.Runtime.Properties;

namespace Il2CppInterop.Runtime
{
    static unsafe class GIBridge
    {
        internal static IntPtr corlib;
        internal static IntPtr UserAssembly = NativeLibrary.Load("UserAssembly", typeof(IL2CPP).Assembly, null);
        internal static IntPtr UnityPlayer = NativeLibrary.Load("UnityPlayer", typeof(IL2CPP).Assembly, null);

        internal static Dictionary<uint, uint> icall_table = new();

        internal const BindingFlags AllBindingFlags =
            BindingFlags.DeclaredOnly |
            BindingFlags.Instance |
            BindingFlags.Static |
            BindingFlags.Public |
            BindingFlags.NonPublic;

        static GIBridge()
        {
            var a = Assembly_Load("mscorlib.dll");
            corlib = il2cpp_assembly_get_image(a);

            using (var reader = new StringReader(Resources.icalltable))
            {
                for (var line = reader.ReadLine(); line != null; line = reader.ReadLine())
                {
                    var entry = line.Split(',');
                    if (entry.Length == 2)
                        icall_table.Add(Convert.ToUInt32(entry[0], 16), Convert.ToUInt32(entry[1], 16));
                }
            }
        }

        public class WrappedArray : WrappedObject, IEnumerable<IntPtr>
        {
            WrappedArray(IntPtr ptr) : base(ptr) { }

            public static new WrappedArray FromPtr(IntPtr ptr) => new(ptr);

            public uint GetLength()
            {
                int i = 0;
                var p = &i;

                var res = CallMethod("GetLength", (IntPtr)p);
                return Unbox<uint>(res.Pointer);
            }

            public IntPtr this[int index]
            {
                get
                {
                    var p = &index;
                    var res = CallMethod("GetValueImpl", (IntPtr)p);
                    return res.Pointer;
                }
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }

            public IEnumerator<IntPtr> GetEnumerator()
            {
                var len = GetLength();
                for (var i = 0; i < len; i++)
                    yield return this[i];
            }
        }

        public class WrappedObject
        {
            protected IntPtr Object;

            public IntPtr Pointer => Object;

            protected WrappedObject(IntPtr ptr)
            {
                Object = ptr;
            }

            public static WrappedObject FromPtr(IntPtr ptr) => new(ptr);

            public WrappedObject? CallMethod(string name, params IntPtr[] args)
            {
                var method = LookupMethod(IL2CPP.il2cpp_object_get_class(Object), name, args.Length);
                if (method == IntPtr.Zero)
                    return null;

                return Invoke(method, Object, false, args);
            }

            public override string ToString()
            {
                var str = CallMethod("ToString").Pointer;
                return IL2CPP.Il2CppStringToManaged(str);
            }
        }

        public static WrappedObject? Invoke(IntPtr method, IntPtr obj, bool convert, params IntPtr[] args)
        {
            fixed (IntPtr* ptr = args)
            {
                var exc = IntPtr.Zero;
                var res = IntPtr.Zero;

                if (convert)
                    res = il2cpp_runtime_invoke_convert_args(method, obj, (void**)ptr, args.Length, ref exc);
                else
                    res = il2cpp_runtime_invoke(method, obj, (void**)ptr, ref exc);

                if (exc != IntPtr.Zero)
                    Logger.Instance.LogError("exception calling method {name}: {exception}",
                        Marshal.PtrToStringAnsi(il2cpp_method_get_name(method)),
                        WrappedObject.FromPtr(exc).ToString());

                return res == IntPtr.Zero ? null : WrappedObject.FromPtr(res);
            }
        }

        public static IntPtr LookupMethod(IntPtr klass, string name, int nargs)
        {
            var method = il2cpp_class_get_method_from_name(klass, name, nargs);
            if (method == IntPtr.Zero)
                Logger.Instance.LogError("Method {name} with {n} args not found", name, nargs);

            return method;
        }

        public static WrappedObject? CallMethodStatic(IntPtr klass, string name, params IntPtr[] args)
        {
            var method = LookupMethod(klass, name, args.Length);
            if (method == IntPtr.Zero)
                return null;

            return Invoke(method, IntPtr.Zero, false, args);
        }

        public static IntPtr ResolveICall(string name)
        {
            static uint fnv1a(string name)
            {
                uint hash = 0x811c9dc5;
                for (var i = 0; i < name.Length; i++)
                {
                    hash ^= (byte)name[i];
                    hash *= 0x1000193;
                }
                return hash;
            }

            var hash = fnv1a(name);
            if (icall_table.TryGetValue(hash, out var result))
                return (IntPtr)((ulong)UnityPlayer + result);
            else
                return IntPtr.Zero;
        }

        public static T Unbox<T>(IntPtr boxed) where T : struct => *(T*)(IL2CPP.il2cpp_object_unbox(boxed));

        private static T GetDelegate<T>(ulong offset) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>((IntPtr)((ulong)UserAssembly + offset));


        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public delegate IntPtr _il2cpp_assembly_get_image(IntPtr assembly);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public delegate IntPtr _il2cpp_class_from_name(IntPtr image, [MarshalAs(UnmanagedType.LPStr)] string namespaze,
            [MarshalAs(UnmanagedType.LPStr)] string name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public delegate IntPtr _il2cpp_class_get_method_from_name(IntPtr klass,
            [MarshalAs(UnmanagedType.LPStr)] string name, int argsCount);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public delegate IntPtr _il2cpp_class_get_methods(IntPtr klass, ref IntPtr iter);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public delegate IntPtr _il2cpp_runtime_invoke_convert_args(IntPtr method, IntPtr obj, void** param,
            int paramCount, ref IntPtr exc);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public delegate IntPtr _il2cpp_runtime_invoke(IntPtr method, IntPtr obj, void** param, ref IntPtr exc);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public delegate IntPtr _il2cpp_value_box(IntPtr klass, IntPtr data);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public delegate IntPtr _il2cpp_string_new(string str);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public delegate IntPtr _il2cpp_string_new_utf16(char* text, int len);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public delegate IntPtr _il2cpp_method_get_name(IntPtr method);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public delegate IntPtr _il2cpp_object_new(IntPtr klass);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public delegate IntPtr _il2cpp_object_get_virtual_method(IntPtr obj, IntPtr method);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public delegate IntPtr _il2cpp_class_from_type(IntPtr type);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public delegate void _il2cpp_field_static_get_value(IntPtr field, void* value);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public delegate IntPtr _il2cpp_domain_get();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public delegate IntPtr _il2cpp_method_get_object(IntPtr method, IntPtr refclass);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public delegate IntPtr _Assembly_Load([MarshalAs(UnmanagedType.LPStr)] string name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public delegate IntPtr _Reflection_GetModuleObject(IntPtr mod);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public delegate IntPtr _Reflection_GetTypeObject(IntPtr type);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public delegate IntPtr _Reflection_GetFieldObject(IntPtr type, IntPtr klass);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public delegate uint _il2cpp_gchandle_new(IntPtr obj, bool pinned);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public delegate IntPtr _il2cpp_gchandle_get_target(uint handle);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public delegate void _il2cpp_gchandle_free(uint handle);

        public static _il2cpp_assembly_get_image il2cpp_assembly_get_image = GetDelegate<_il2cpp_assembly_get_image>(0x885850);
        public static _il2cpp_class_from_name il2cpp_class_from_name = GetDelegate<_il2cpp_class_from_name>(0x87CCD0);
        public static _il2cpp_class_get_method_from_name il2cpp_class_get_method_from_name = GetDelegate<_il2cpp_class_get_method_from_name>(0x8866C0);
        public static _il2cpp_class_get_methods il2cpp_class_get_methods = GetDelegate<_il2cpp_class_get_methods>(0x887160);
        public static _il2cpp_runtime_invoke_convert_args il2cpp_runtime_invoke_convert_args = GetDelegate<_il2cpp_runtime_invoke_convert_args>(0x890870);
        public static _il2cpp_runtime_invoke il2cpp_runtime_invoke = GetDelegate<_il2cpp_runtime_invoke>(0x83E730);
        public static _il2cpp_value_box il2cpp_value_box = GetDelegate<_il2cpp_value_box>(0x87C5E0);
        public static _il2cpp_string_new il2cpp_string_new = GetDelegate<_il2cpp_string_new>(0x8951F0);
        public static _il2cpp_string_new_utf16 il2cpp_string_new_utf16 = GetDelegate<_il2cpp_string_new_utf16>(0x895880);
        public static _il2cpp_method_get_name il2cpp_method_get_name = GetDelegate<_il2cpp_method_get_name>(0x886AC0);
        public static _il2cpp_object_new il2cpp_object_new = GetDelegate<_il2cpp_object_new>(0x895270);
        public static _il2cpp_object_get_virtual_method il2cpp_object_get_virtual_method = GetDelegate<_il2cpp_object_get_virtual_method>(0x88C200);
        public static _il2cpp_class_from_type il2cpp_class_from_type = GetDelegate<_il2cpp_class_from_type>(0x87F6B0);
        public static _il2cpp_field_static_get_value il2cpp_field_static_get_value = GetDelegate<_il2cpp_field_static_get_value>(0x89CA30);
        public static _il2cpp_domain_get il2cpp_domain_get = GetDelegate<_il2cpp_domain_get>(0x882E00);

        public static _il2cpp_gchandle_new il2cpp_gchandle_new = GetDelegate<_il2cpp_gchandle_new>(0x94A9F0);
        public static _il2cpp_gchandle_get_target il2cpp_gchandle_get_target = GetDelegate<_il2cpp_gchandle_get_target>(0x94A6D0);
        public static _il2cpp_gchandle_free il2cpp_gchandle_free = GetDelegate<_il2cpp_gchandle_free>(0x94A380);

        public static _Assembly_Load Assembly_Load = GetDelegate<_Assembly_Load>(0x8937B0);
        public static _Reflection_GetModuleObject Reflection_GetModuleObject = GetDelegate<_Reflection_GetModuleObject>(0x887230);
        public static _Reflection_GetTypeObject Reflection_GetTypeObject = GetDelegate<_Reflection_GetTypeObject>(0x88B640);
        public static _Reflection_GetFieldObject Reflection_GetFieldObject = GetDelegate<_Reflection_GetFieldObject>(0x865210);
        public static _il2cpp_method_get_object il2cpp_method_get_object = GetDelegate<_il2cpp_method_get_object>(0x886B90);
    }
}
