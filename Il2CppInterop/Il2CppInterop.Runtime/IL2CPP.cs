using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Il2CppInterop.Common;
using Il2CppInterop.Common.Attributes;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppInterop.Runtime.Runtime;
using Microsoft.Extensions.Logging;

namespace Il2CppInterop.Runtime;

public static unsafe class IL2CPP
{
    private static readonly Dictionary<string, IntPtr> ourImagesMap = new();

    static IL2CPP()
    {
        var domain = il2cpp_domain_get();
        if (domain == IntPtr.Zero)
        {
            Logger.Instance.LogError("No il2cpp domain found; sad!");
            return;
        }

        uint assembliesCount = 0;
        var assemblies = il2cpp_domain_get_assemblies(domain, ref assembliesCount);
        for (var i = 0; i < assembliesCount; i++)
        {
            var image = il2cpp_assembly_get_image(assemblies[i]);
            var name = Marshal.PtrToStringAnsi(il2cpp_image_get_name(image));
            ourImagesMap[name] = image;
        }
    }

    internal static IntPtr GetIl2CppImage(string name)
    {
        if (ourImagesMap.ContainsKey(name)) return ourImagesMap[name];
        return IntPtr.Zero;
    }

    internal static IntPtr[] GetIl2CppImages()
    {
        return ourImagesMap.Values.ToArray();
    }

    public static IntPtr GetIl2CppClass(string assemblyName, string namespaze, string className)
    {
        if (!ourImagesMap.TryGetValue(assemblyName, out var image))
        {
            Logger.Instance.LogError("Assembly {AssemblyName} is not registered in il2cpp", assemblyName);
            return IntPtr.Zero;
        }

        var clazz = il2cpp_class_from_name(image, namespaze, className);
        return clazz;
    }

    public static IntPtr GetIl2CppField(IntPtr clazz, string fieldName)
    {
        if (clazz == IntPtr.Zero) return IntPtr.Zero;

        var field = il2cpp_class_get_field_from_name(clazz, fieldName);
        if (field == IntPtr.Zero)
            Logger.Instance.LogError(
                "Field {FieldName} was not found on class {ClassName}", fieldName, Marshal.PtrToStringUTF8(il2cpp_class_get_name(clazz)));
        return field;
    }

    public static IntPtr GetIl2CppMethodByToken(IntPtr clazz, int token)
    {
        if (clazz == IntPtr.Zero)
            return NativeStructUtils.GetMethodInfoForMissingMethod(token.ToString());

        var iter = IntPtr.Zero;
        IntPtr method;
        while ((method = il2cpp_class_get_methods(clazz, ref iter)) != IntPtr.Zero)
            if (il2cpp_method_get_token(method) == token)
                return method;

        var className = Marshal.PtrToStringAnsi(il2cpp_class_get_name(clazz));
        Logger.Instance.LogTrace("Unable to find method {ClassName}::{Token}", className, token);

        return NativeStructUtils.GetMethodInfoForMissingMethod(className + "::" + token);
    }

    public static IntPtr GetIl2CppMethod(IntPtr clazz, bool isGeneric, string methodName, string returnTypeName,
        params string[] argTypes)
    {
        if (clazz == IntPtr.Zero)
            return NativeStructUtils.GetMethodInfoForMissingMethod(methodName + "(" + string.Join(", ", argTypes) +
                                                                   ")");

        returnTypeName = Regex.Replace(returnTypeName, "\\`\\d+", "").Replace('/', '.').Replace('+', '.');
        for (var index = 0; index < argTypes.Length; index++)
        {
            var argType = argTypes[index];
            argTypes[index] = Regex.Replace(argType, "\\`\\d+", "").Replace('/', '.').Replace('+', '.');
        }

        var methodsSeen = 0;
        var lastMethod = IntPtr.Zero;
        var iter = IntPtr.Zero;
        IntPtr method;
        while ((method = il2cpp_class_get_methods(clazz, ref iter)) != IntPtr.Zero)
        {
            if (Marshal.PtrToStringAnsi(il2cpp_method_get_name(method)) != methodName)
                continue;

            if (il2cpp_method_get_param_count(method) != argTypes.Length)
                continue;

            if (il2cpp_method_is_generic(method) != isGeneric)
                continue;

            var returnType = il2cpp_method_get_return_type(method);
            var returnTypeNameActual = Marshal.PtrToStringAnsi(il2cpp_type_get_name(returnType));
            if (returnTypeNameActual != returnTypeName)
                continue;

            methodsSeen++;
            lastMethod = method;

            var badType = false;
            for (var i = 0; i < argTypes.Length; i++)
            {
                var paramType = il2cpp_method_get_param(method, (uint)i);
                var typeName = Marshal.PtrToStringAnsi(il2cpp_type_get_name(paramType));
                if (typeName != argTypes[i])
                {
                    badType = true;
                    break;
                }
            }

            if (badType) continue;

            return method;
        }

        var className = Marshal.PtrToStringAnsi(il2cpp_class_get_name(clazz));

        if (methodsSeen == 1)
        {
            Logger.Instance.LogTrace(
                "Method {ClassName}::{MethodName} was stubbed with a random matching method of the same name", className, methodName);
            Logger.Instance.LogTrace(
                "Stubby return type/target: {LastMethod} / {ReturnTypeName}", Marshal.PtrToStringUTF8(il2cpp_type_get_name(il2cpp_method_get_return_type(lastMethod))), returnTypeName);
            Logger.Instance.LogTrace("Stubby parameter types/targets follow:");
            for (var i = 0; i < argTypes.Length; i++)
            {
                var paramType = il2cpp_method_get_param(lastMethod, (uint)i);
                var typeName = Marshal.PtrToStringAnsi(il2cpp_type_get_name(paramType));
                Logger.Instance.LogTrace("    {TypeName} / {ArgType}", typeName, argTypes[i]);
            }

            return lastMethod;
        }

        Logger.Instance.LogTrace("Unable to find method {ClassName}::{MethodName}; signature follows", className, methodName);
        Logger.Instance.LogTrace("    return {ReturnTypeName}", returnTypeName);
        foreach (var argType in argTypes)
            Logger.Instance.LogTrace("    {ArgType}", argType);
        Logger.Instance.LogTrace("Available methods of this name follow:");
        iter = IntPtr.Zero;
        while ((method = il2cpp_class_get_methods(clazz, ref iter)) != IntPtr.Zero)
        {
            if (Marshal.PtrToStringAnsi(il2cpp_method_get_name(method)) != methodName)
                continue;

            var nParams = il2cpp_method_get_param_count(method);
            Logger.Instance.LogTrace("Method starts");
            Logger.Instance.LogTrace(
                "     return {MethodTypeName}", Marshal.PtrToStringUTF8(il2cpp_type_get_name(il2cpp_method_get_return_type(method))));
            for (var i = 0; i < nParams; i++)
            {
                var paramType = il2cpp_method_get_param(method, (uint)i);
                var typeName = Marshal.PtrToStringAnsi(il2cpp_type_get_name(paramType));
                Logger.Instance.LogTrace("    {TypeName}", typeName);
            }

            return method;
        }

        return NativeStructUtils.GetMethodInfoForMissingMethod(className + "::" + methodName + "(" +
                                                               string.Join(", ", argTypes) + ")");
    }

    public static string? Il2CppStringToManaged(IntPtr il2CppString)
    {
        if (il2CppString == IntPtr.Zero) return null;

        var length = il2cpp_string_length(il2CppString);
        var chars = il2cpp_string_chars(il2CppString);

        return new string(chars, 0, length);
    }

    public static IntPtr ManagedStringToIl2Cpp(string str)
    {
        if (str == null) return IntPtr.Zero;

        fixed (char* chars = str)
        {
            return il2cpp_string_new_utf16(chars, str.Length);
        }
    }

    public static IntPtr Il2CppObjectBaseToPtr(Il2CppObjectBase obj)
    {
        return obj?.Pointer ?? IntPtr.Zero;
    }

    public static IntPtr Il2CppObjectBaseToPtrNotNull(Il2CppObjectBase obj)
    {
        return obj?.Pointer ?? throw new NullReferenceException();
    }

    public static IntPtr GetIl2CppNestedType(IntPtr enclosingType, string nestedTypeName)
    {
        if (enclosingType == IntPtr.Zero) return IntPtr.Zero;
        return RuntimeReflectionHelper.GetNestedTypeViaReflection(enclosingType, nestedTypeName);

        var iter = IntPtr.Zero;
        IntPtr nestedTypePtr;
        if (il2cpp_class_is_inflated(enclosingType))
        {
            Logger.Instance.LogTrace("Original class was inflated, falling back to reflection");

            return RuntimeReflectionHelper.GetNestedTypeViaReflection(enclosingType, nestedTypeName);
        }

        while ((nestedTypePtr = il2cpp_class_get_nested_types(enclosingType, ref iter)) != IntPtr.Zero)
            if (Marshal.PtrToStringAnsi(il2cpp_class_get_name(nestedTypePtr)) == nestedTypeName)
                return nestedTypePtr;

        Logger.Instance.LogError(
            "Nested type {NestedTypeName} on {EnclosingTypeName} not found!", nestedTypeName, Marshal.PtrToStringUTF8(il2cpp_class_get_name(enclosingType)));

        return IntPtr.Zero;
    }

    public static void ThrowIfNull(object arg)
    {
        if (arg == null)
            throw new NullReferenceException();
    }

    public static T ResolveICall<T>(string signature) where T : Delegate
    {
        var icallPtr = il2cpp_resolve_icall(signature);
        if (icallPtr == IntPtr.Zero)
        {
            Logger.Instance.LogTrace("ICall {Signature} not resolved", signature);
            return GenerateDelegateForMissingICall<T>(signature);
        }

        return Marshal.GetDelegateForFunctionPointer<T>(icallPtr);
    }

    private static T GenerateDelegateForMissingICall<T>(string signature) where T : Delegate
    {
        var invoke = typeof(T).GetMethod("Invoke")!;

        var trampoline = new DynamicMethod("(missing icall delegate) " + typeof(T).FullName,
            invoke.ReturnType, invoke.GetParameters().Select(it => it.ParameterType).ToArray(), typeof(IL2CPP), true);
        var bodyBuilder = trampoline.GetILGenerator();

        bodyBuilder.Emit(OpCodes.Ldstr, $"ICall with signature {signature} was not resolved");
        bodyBuilder.Emit(OpCodes.Newobj, typeof(Exception).GetConstructor(new[] { typeof(string) })!);
        bodyBuilder.Emit(OpCodes.Throw);

        return (T)trampoline.CreateDelegate(typeof(T));
    }

    public static T? PointerToValueGeneric<T>(IntPtr objectPointer, bool isFieldPointer, bool valueTypeWouldBeBoxed)
    {
        if (isFieldPointer)
        {
            if (il2cpp_class_is_valuetype(Il2CppClassPointerStore<T>.NativeClassPtr))
                objectPointer = il2cpp_value_box(Il2CppClassPointerStore<T>.NativeClassPtr, objectPointer);
            else
                objectPointer = *(IntPtr*)objectPointer;
        }

        if (!valueTypeWouldBeBoxed && il2cpp_class_is_valuetype(Il2CppClassPointerStore<T>.NativeClassPtr))
            objectPointer = il2cpp_value_box(Il2CppClassPointerStore<T>.NativeClassPtr, objectPointer);

        if (typeof(T) == typeof(string))
            return (T)(object)Il2CppStringToManaged(objectPointer);

        if (objectPointer == IntPtr.Zero)
            return default;

        if (typeof(T).IsValueType)
            return Il2CppObjectBase.UnboxUnsafe<T>(objectPointer);

        var il2CppObjectBase = Il2CppObjectBase.CreateUnsafe<T>(objectPointer);
        return Unsafe.As<Il2CppObjectBase, T>(ref il2CppObjectBase);
    }

    public static string RenderTypeName<T>(bool addRefMarker = false)
    {
        return RenderTypeName(typeof(T), addRefMarker);
    }

    public static string RenderTypeName(Type t, bool addRefMarker = false)
    {
        if (addRefMarker) return RenderTypeName(t) + "&";
        if (t.IsArray) return RenderTypeName(t.GetElementType()) + "[]";
        if (t.IsByRef) return RenderTypeName(t.GetElementType()) + "&";
        if (t.IsPointer) return RenderTypeName(t.GetElementType()) + "*";
        if (t.IsGenericParameter) return t.Name;

        if (t.IsGenericType)
        {
            if (t.TypeHasIl2CppArrayBase())
                return RenderTypeName(t.GetGenericArguments()[0]) + "[]";

            var builder = new StringBuilder();
            builder.Append(t.GetGenericTypeDefinition().FullNameObfuscated().TrimIl2CppPrefix());
            builder.Append('<');
            var genericArguments = t.GetGenericArguments();
            for (var i = 0; i < genericArguments.Length; i++)
            {
                if (i != 0) builder.Append(',');
                builder.Append(RenderTypeName(genericArguments[i]));
            }

            builder.Append('>');
            return builder.ToString();
        }

        if (t == typeof(Il2CppStringArray))
            return "System.String[]";

        return t.FullNameObfuscated().TrimIl2CppPrefix();
    }

    private static string FullNameObfuscated(this Type t)
    {
        var obfuscatedNameAnnotations = t.GetCustomAttribute<ObfuscatedNameAttribute>();
        if (obfuscatedNameAnnotations == null) return t.FullName;
        return obfuscatedNameAnnotations.ObfuscatedName;
    }

    private static string TrimIl2CppPrefix(this string s)
    {
        return s.StartsWith("Il2Cpp") ? s.Substring("Il2Cpp".Length) : s;
    }

    private static bool TypeHasIl2CppArrayBase(this Type type)
    {
        if (type == null) return false;
        if (type.IsConstructedGenericType) type = type.GetGenericTypeDefinition();
        if (type == typeof(Il2CppArrayBase<>)) return true;
        return TypeHasIl2CppArrayBase(type.BaseType);
    }

    // this is called if there's no actual il2cpp_gc_wbarrier_set_field()
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void FieldWriteWbarrierStub(IntPtr obj, IntPtr targetAddress, IntPtr value)
    {
        // ignore obj
        *(IntPtr*)targetAddress = value;
    }
    

    // implementations
    public static IntPtr il2cpp_assembly_get_image(IntPtr assembly) => GIBridge.il2cpp_assembly_get_image(assembly);
    public static IntPtr il2cpp_domain_get() => GIBridge.il2cpp_domain_get();
    public static void il2cpp_field_static_get_value(IntPtr field, void* value) => GIBridge.il2cpp_field_static_get_value(field, value);
    public static IntPtr il2cpp_runtime_invoke(IntPtr method, IntPtr obj, void** param, ref IntPtr exc) =>
        GIBridge.il2cpp_runtime_invoke(method, obj, param, ref exc);
    public static IntPtr il2cpp_runtime_invoke_convert_args(IntPtr method, IntPtr obj, void** param,
        int paramCount, ref IntPtr exc) => GIBridge.il2cpp_runtime_invoke_convert_args(method, obj, param, paramCount, ref exc);
    public static IntPtr il2cpp_class_from_type(IntPtr type) => GIBridge.il2cpp_class_from_type(type);
    public static IntPtr il2cpp_method_get_name(IntPtr method) => GIBridge.il2cpp_method_get_name(method);
    public static IntPtr il2cpp_class_from_name(IntPtr image, [MarshalAs(UnmanagedType.LPStr)] string namespaze,
        [MarshalAs(UnmanagedType.LPStr)] string name) => GIBridge.il2cpp_class_from_name(image, namespaze, name);
    public static IntPtr il2cpp_class_get_method_from_name(IntPtr klass, string name, int argsCount) =>
        GIBridge.il2cpp_class_get_method_from_name(klass, name, argsCount);
    public static IntPtr il2cpp_object_new(IntPtr klass) => GIBridge.il2cpp_object_new(klass);
    public static IntPtr il2cpp_value_box(IntPtr klass, IntPtr obj) => GIBridge.il2cpp_value_box(klass, obj);
    public static IntPtr il2cpp_string_new(string str) => GIBridge.il2cpp_string_new(str);
    public static IntPtr il2cpp_string_new_utf16(char* text, int len) => GIBridge.il2cpp_string_new_utf16(text, len);
    public static IntPtr il2cpp_class_get_methods(IntPtr klass, ref IntPtr iter) => GIBridge.il2cpp_class_get_methods(klass, ref iter);
    public static IntPtr il2cpp_object_get_virtual_method(IntPtr obj, IntPtr method) => GIBridge.il2cpp_object_get_virtual_method(obj, method);
    public static IntPtr il2cpp_method_get_object(IntPtr method, IntPtr refclass) => GIBridge.il2cpp_method_get_object(method, refclass);
    public static uint il2cpp_gchandle_new(IntPtr obj, bool pinned) => GIBridge.il2cpp_gchandle_new(obj, pinned);
    public static IntPtr il2cpp_gchandle_get_target(uint gchandle) => GIBridge.il2cpp_gchandle_get_target(gchandle);
    public static void il2cpp_gchandle_free(uint gchandle) => GIBridge.il2cpp_gchandle_free(gchandle);

    public static IntPtr il2cpp_resolve_icall(string name) => GIBridge.ResolveICall(name);

    public static IntPtr il2cpp_image_get_name(IntPtr image)
    {
        var mod = GIBridge.Reflection_GetModuleObject(image);
        var obj = GIBridge.WrappedObject.FromPtr(mod);
        var name = obj.ToString();
        return Marshal.StringToHGlobalAnsi(name); // mem leak
    }

    public static uint il2cpp_method_get_token(IntPtr method) => UnityVersionHandler.Wrap((Il2CppMethodInfo*)method).Token;

    public static void il2cpp_runtime_class_init(IntPtr klass) => il2cpp_object_new(klass); // will call Runtime::ClassInit

    public static void il2cpp_field_static_set_value(IntPtr field, void* value)
    {
        // MonoField.SetValueInternal(field, null, value)
        var f = GIBridge.Reflection_GetFieldObject(field, IntPtr.Zero);
        GIBridge.CallMethodStatic(il2cpp_object_get_class(f), "SetValueInternal", f, IntPtr.Zero, (IntPtr)value);
    }

    public static uint il2cpp_field_get_offset(IntPtr field) => (uint)UnityVersionHandler.Wrap((Il2CppFieldInfo*)field).Offset ^ 0x21C59724;

    public static int il2cpp_string_length(IntPtr str) => ((Il2CppString*)str)->len;

    public static char* il2cpp_string_chars(IntPtr str) => &((Il2CppString*)str)->chars;

    public static IntPtr il2cpp_object_get_class(IntPtr obj) => ((Il2CppObject*)obj)->data;

    public static IntPtr il2cpp_object_unbox(IntPtr obj) => obj + sizeof(Il2CppObject);

    public static IntPtr* il2cpp_domain_get_assemblies(IntPtr domain, ref uint size)
    {
        var AppDomain = il2cpp_class_from_name(GIBridge.corlib, "System", "AppDomain");
        var asms = GIBridge.CallMethodStatic(AppDomain, "GetAssemblies");
        var asms_array = GIBridge.WrappedArray.FromPtr(asms.Pointer);

        size = asms_array.GetLength();
        IntPtr* assemblies = (IntPtr*)Marshal.AllocHGlobal((int)size * sizeof(IntPtr));
        for (var i = 0; i < size; i++)
            assemblies[i] = (IntPtr)((Il2CppReflectionAssembly*)asms_array[i])->assembly;

        return assemblies;
    }

    public static IntPtr il2cpp_array_new(IntPtr elementTypeInfo, ulong length)
    {
        // Array.CreateInstance(type, length)
        var t = il2cpp_class_get_type(elementTypeInfo);
        var type = GIBridge.Reflection_GetTypeObject(t);

        var system_array = il2cpp_class_from_name(GIBridge.corlib, "System", "Array");

        var len = (uint)length;
        var pLen = &len;
        return GIBridge.CallMethodStatic(system_array, "CreateInstance", type, (IntPtr)pLen).Pointer;
    }

    public static uint il2cpp_array_length(IntPtr array) => GIBridge.WrappedArray.FromPtr(array).GetLength();

    public static IntPtr il2cpp_array_class_get(IntPtr element_class, uint rank)
    {
        // type.MakeArrayType(rank)
        var elem = GIBridge.Reflection_GetTypeObject(il2cpp_class_get_type(element_class));

        var pRank = &rank;
        var array_type = GIBridge.WrappedObject.FromPtr(elem).CallMethod("MakeArrayType", (IntPtr)pRank);
        var type = ((Il2CppReflectionType*)array_type.Pointer)->type;
        return il2cpp_class_from_type((IntPtr)type);
    }

    public static IntPtr il2cpp_class_get_type(IntPtr klass) => UnityVersionHandler.Wrap((Il2CppClass*)klass).ByValArg.Pointer;

    public static IntPtr il2cpp_class_from_system_type(IntPtr type) => il2cpp_class_from_type((IntPtr)((Il2CppReflectionType*)type)->type);

    public static int il2cpp_class_value_size(IntPtr klass, ref uint align)
    {
        //align = 1;
        return (int)UnityVersionHandler.Wrap((Il2CppClass*)klass).InstanceSize - sizeof(Il2CppObject);
    }

    public static bool il2cpp_class_is_assignable_from(IntPtr klass, IntPtr oklass)
    {
        // klass.IsAssignableFrom(oklass)
        var t1 = GIBridge.Reflection_GetTypeObject(il2cpp_class_get_type(klass));
        var t2 = GIBridge.Reflection_GetTypeObject(il2cpp_class_get_type(oklass));
        var res = GIBridge.WrappedObject.FromPtr(t1).CallMethod("IsAssignableFrom", t2);
        return GIBridge.Unbox<bool>(res.Pointer);
    }

    public static bool il2cpp_class_is_valuetype(IntPtr klass) => UnityVersionHandler.Wrap((Il2CppClass*)klass).ValueType;

    public static IntPtr il2cpp_class_get_field_from_name(IntPtr klass, string name)
    {
        var t = il2cpp_class_get_type(klass);
        var type = GIBridge.Reflection_GetTypeObject(t);

        var flags = (uint)GIBridge.AllBindingFlags;
        var pFlags = &flags;

        var field = GIBridge.WrappedObject.FromPtr(type).CallMethod("GetField", il2cpp_string_new(name), (IntPtr)pFlags);
        return (IntPtr)((Il2CppReflectionField*)field.Pointer)->field;
    }

    public static void il2cpp_format_exception(IntPtr ex, void* message, int message_size)
    {
        var str = GIBridge.WrappedObject.FromPtr(ex).ToString();
        var size = Math.Min(message_size, str.Length);
        var p = (byte*)message;
        for (var i = 0; i < size; i++)
            p[i] = (byte)str[i];
    }

    #region UNIMPLEMENTED
    // IL2CPP Functions
    public static void il2cpp_init(IntPtr domain_name) => throw new NotImplementedException();
    
    public static void il2cpp_init_utf16(IntPtr domain_name) => throw new NotImplementedException();
    
    public static void il2cpp_shutdown() => throw new NotImplementedException();
    
    public static void il2cpp_set_config_dir(IntPtr config_path) => throw new NotImplementedException();
    
    public static void il2cpp_set_data_dir(IntPtr data_path) => throw new NotImplementedException();
    
    public static void il2cpp_set_temp_dir(IntPtr temp_path) => throw new NotImplementedException();
    
    public static void il2cpp_set_commandline_arguments(int argc, IntPtr argv, IntPtr basedir) => throw new NotImplementedException();
    
    public static void il2cpp_set_commandline_arguments_utf16(int argc, IntPtr argv, IntPtr basedir) => throw new NotImplementedException();
    
    public static void il2cpp_set_config_utf16(IntPtr executablePath) => throw new NotImplementedException();
    
    public static void il2cpp_set_config(IntPtr executablePath) => throw new NotImplementedException();
    
    public static void il2cpp_set_memory_callbacks(IntPtr callbacks) => throw new NotImplementedException();
    
    public static IntPtr il2cpp_get_corlib() => throw new NotImplementedException();
    
    public static void il2cpp_add_internal_call(IntPtr name, IntPtr method) => throw new NotImplementedException();
    
    //public static IntPtr il2cpp_resolve_icall([MarshalAs(UnmanagedType.LPStr)] string name) => throw new NotImplementedException();
    
    public static IntPtr il2cpp_alloc(uint size) => throw new NotImplementedException();
    
    public static void il2cpp_free(IntPtr ptr) => throw new NotImplementedException();
    
    //public static IntPtr il2cpp_array_class_get(IntPtr element_class, uint rank) => throw new NotImplementedException();
    
    //public static uint il2cpp_array_length(IntPtr array) => throw new NotImplementedException();
    
    public static uint il2cpp_array_get_byte_length(IntPtr array) => throw new NotImplementedException();
    
    //public static IntPtr il2cpp_array_new(IntPtr elementTypeInfo, ulong length) => throw new NotImplementedException();
    
    public static IntPtr il2cpp_array_new_specific(IntPtr arrayTypeInfo, ulong length) => throw new NotImplementedException();
    
    public static IntPtr il2cpp_array_new_full(IntPtr array_class, ref ulong lengths, ref ulong lower_bounds) => throw new NotImplementedException();
    
    public static IntPtr il2cpp_bounded_array_class_get(IntPtr element_class, uint rank, bool bounded) => throw new NotImplementedException();
    
    public static int il2cpp_array_element_size(IntPtr array_class) => throw new NotImplementedException();
    
    //public static IntPtr il2cpp_assembly_get_image(IntPtr assembly) => throw new NotImplementedException();
    
    public static IntPtr il2cpp_class_enum_basetype(IntPtr klass) => throw new NotImplementedException();
    
    public static bool il2cpp_class_is_generic(IntPtr klass) => throw new NotImplementedException();
    
    public static bool il2cpp_class_is_inflated(IntPtr klass) => throw new NotImplementedException();
    
    //public static bool il2cpp_class_is_assignable_from(IntPtr klass, IntPtr oklass) => throw new NotImplementedException();
    
    public static bool il2cpp_class_is_subclass_of(IntPtr klass, IntPtr klassc,
    [MarshalAs(UnmanagedType.I1)] bool check_interfaces) => throw new NotImplementedException();
    
    public static bool il2cpp_class_has_parent(IntPtr klass, IntPtr klassc) => throw new NotImplementedException();
    
    public static IntPtr il2cpp_class_from_il2cpp_type(IntPtr type) => throw new NotImplementedException();
    
    //public static IntPtr il2cpp_class_from_name(IntPtr image, [MarshalAs(UnmanagedType.LPStr)] string namespaze,
    //[MarshalAs(UnmanagedType.LPStr)] string name) => throw new NotImplementedException();
    
    //public static IntPtr il2cpp_class_from_system_type(IntPtr type) => throw new NotImplementedException();
    
    public static IntPtr il2cpp_class_get_element_class(IntPtr klass) => throw new NotImplementedException();
    
    public static IntPtr il2cpp_class_get_events(IntPtr klass, ref IntPtr iter) => throw new NotImplementedException();
    
    public static IntPtr il2cpp_class_get_fields(IntPtr klass, ref IntPtr iter) => throw new NotImplementedException();
    
    public static IntPtr il2cpp_class_get_nested_types(IntPtr klass, ref IntPtr iter) => throw new NotImplementedException();
    
    public static IntPtr il2cpp_class_get_interfaces(IntPtr klass, ref IntPtr iter) => throw new NotImplementedException();
    
    public static IntPtr il2cpp_class_get_properties(IntPtr klass, ref IntPtr iter) => throw new NotImplementedException();
    
    public static IntPtr il2cpp_class_get_property_from_name(IntPtr klass, IntPtr name) => throw new NotImplementedException();
    
    //public static IntPtr il2cpp_class_get_field_from_name(IntPtr klass, string name) => throw new NotImplementedException();
    
    //public static IntPtr il2cpp_class_get_methods(IntPtr klass, ref IntPtr iter) => throw new NotImplementedException();
    
    //public static IntPtr il2cpp_class_get_method_from_name(IntPtr klass, string name, int argsCount) => throw new NotImplementedException();
    
    public static IntPtr il2cpp_class_get_name(IntPtr klass) => throw new NotImplementedException();
    
    public static IntPtr il2cpp_class_get_namespace(IntPtr klass) => throw new NotImplementedException();
    
    public static IntPtr il2cpp_class_get_parent(IntPtr klass) => throw new NotImplementedException();
    
    public static IntPtr il2cpp_class_get_declaring_type(IntPtr klass) => throw new NotImplementedException();
    
    public static int il2cpp_class_instance_size(IntPtr klass) => throw new NotImplementedException();
    
    public static uint il2cpp_class_num_fields(IntPtr enumKlass) => throw new NotImplementedException();
    
    //public static bool il2cpp_class_is_valuetype(IntPtr klass) => throw new NotImplementedException();
    
    //public static int il2cpp_class_value_size(IntPtr klass, ref uint align) => throw new NotImplementedException();
    
    public static bool il2cpp_class_is_blittable(IntPtr klass) => throw new NotImplementedException();
    
    public static int il2cpp_class_get_flags(IntPtr klass) => throw new NotImplementedException();
    
    public static bool il2cpp_class_is_abstract(IntPtr klass) => throw new NotImplementedException();
    
    public static bool il2cpp_class_is_interface(IntPtr klass) => throw new NotImplementedException();
    
    public static int il2cpp_class_array_element_size(IntPtr klass) => throw new NotImplementedException();
    
    //public static IntPtr il2cpp_class_from_type(IntPtr type) => throw new NotImplementedException();
    
    //public static IntPtr il2cpp_class_get_type(IntPtr klass) => throw new NotImplementedException();
    
    public static uint il2cpp_class_get_type_token(IntPtr klass) => throw new NotImplementedException();
    
    public static bool il2cpp_class_has_attribute(IntPtr klass, IntPtr attr_class) => throw new NotImplementedException();
    
    public static bool il2cpp_class_has_references(IntPtr klass) => throw new NotImplementedException();
    
    public static bool il2cpp_class_is_enum(IntPtr klass) => throw new NotImplementedException();
    
    public static IntPtr il2cpp_class_get_image(IntPtr klass) => throw new NotImplementedException();
    
    public static IntPtr il2cpp_class_get_assemblyname(IntPtr klass) => throw new NotImplementedException();
    
    public static int il2cpp_class_get_rank(IntPtr klass) => throw new NotImplementedException();
    
    public static uint il2cpp_class_get_bitmap_size(IntPtr klass) => throw new NotImplementedException();
    
    public static void il2cpp_class_get_bitmap(IntPtr klass, ref uint bitmap) => throw new NotImplementedException();
    
    public static bool il2cpp_stats_dump_to_file(IntPtr path) => throw new NotImplementedException();
    
//    //public extern static ulong il2cpp_stats_get_value(IL2CPP_Stat stat);
    //public static IntPtr il2cpp_domain_get() => throw new NotImplementedException();
    
    public static IntPtr il2cpp_domain_assembly_open(IntPtr domain, IntPtr name) => throw new NotImplementedException();
    
    //public static IntPtr* il2cpp_domain_get_assemblies(IntPtr domain, ref uint size) => throw new NotImplementedException();

    public static IntPtr il2cpp_exception_from_name_msg(IntPtr image, IntPtr name_space, IntPtr name, IntPtr msg) => throw new NotImplementedException();
    
    public static IntPtr il2cpp_get_exception_argument_null(IntPtr arg) => throw new NotImplementedException();
    
    //public static void il2cpp_format_exception(IntPtr ex, void* message, int message_size) => throw new NotImplementedException();
    
    public static void il2cpp_format_stack_trace(IntPtr ex, void* output, int output_size) => throw new NotImplementedException();
    
    public static void il2cpp_unhandled_exception(IntPtr ex) => throw new NotImplementedException();
    
    public static int il2cpp_field_get_flags(IntPtr field) => throw new NotImplementedException();
    
    public static IntPtr il2cpp_field_get_name(IntPtr field) => throw new NotImplementedException();
    
    public static IntPtr il2cpp_field_get_parent(IntPtr field) => throw new NotImplementedException();
    
    //public static uint il2cpp_field_get_offset(IntPtr field) => throw new NotImplementedException();
    
    public static IntPtr il2cpp_field_get_type(IntPtr field) => throw new NotImplementedException();
    
    public static void il2cpp_field_get_value(IntPtr obj, IntPtr field, void* value) => throw new NotImplementedException();
    
    public static IntPtr il2cpp_field_get_value_object(IntPtr field, IntPtr obj) => throw new NotImplementedException();
    
    public static bool il2cpp_field_has_attribute(IntPtr field, IntPtr attr_class) => throw new NotImplementedException();
    
    public static void il2cpp_field_set_value(IntPtr obj, IntPtr field, void* value) => throw new NotImplementedException();
    
    //public static void il2cpp_field_static_get_value(IntPtr field, void* value) => throw new NotImplementedException();

    //public static void il2cpp_field_static_set_value(IntPtr field, void* value) => throw new NotImplementedException();

    public static void il2cpp_field_set_value_object(IntPtr instance, IntPtr field, IntPtr value) => throw new NotImplementedException();
    
    public static void il2cpp_gc_collect(int maxGenerations) => throw new NotImplementedException();
    
    public static int il2cpp_gc_collect_a_little() => throw new NotImplementedException();
    
    public static void il2cpp_gc_disable() => throw new NotImplementedException();
    
    public static void il2cpp_gc_enable() => throw new NotImplementedException();
    
    public static bool il2cpp_gc_is_disabled() => throw new NotImplementedException();
    
    public static long il2cpp_gc_get_used_size() => throw new NotImplementedException();
    
    public static long il2cpp_gc_get_heap_size() => throw new NotImplementedException();
    
    public static void il2cpp_gc_wbarrier_set_field(IntPtr obj, IntPtr targetAddress, IntPtr gcObj) => throw new NotImplementedException();
    
    //public static uint il2cpp_gchandle_new(IntPtr obj, [MarshalAs(UnmanagedType.I1)] bool pinned) => throw new NotImplementedException();
    
    public static uint il2cpp_gchandle_new_weakref(IntPtr obj,
    [MarshalAs(UnmanagedType.I1)] bool track_resurrection) => throw new NotImplementedException();
    
    //public static IntPtr il2cpp_gchandle_get_target(uint gchandle) => throw new NotImplementedException();
    
    //public static void il2cpp_gchandle_free(uint gchandle) => throw new NotImplementedException();
    
    public static IntPtr il2cpp_unity_liveness_calculation_begin(IntPtr filter, int max_object_count,
    IntPtr callback, IntPtr userdata, IntPtr onWorldStarted, IntPtr onWorldStopped) => throw new NotImplementedException();
    
    public static void il2cpp_unity_liveness_calculation_end(IntPtr state) => throw new NotImplementedException();
    
    public static void il2cpp_unity_liveness_calculation_from_root(IntPtr root, IntPtr state) => throw new NotImplementedException();
    
    public static void il2cpp_unity_liveness_calculation_from_statics(IntPtr state) => throw new NotImplementedException();
    
    public static IntPtr il2cpp_method_get_return_type(IntPtr method) => throw new NotImplementedException();
    
    public static IntPtr il2cpp_method_get_declaring_type(IntPtr method) => throw new NotImplementedException();
    
    //public static IntPtr il2cpp_method_get_name(IntPtr method) => throw new NotImplementedException();
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IntPtr _il2cpp_method_get_from_reflection(IntPtr method)
    {
        if (UnityVersionHandler.HasGetMethodFromReflection) return il2cpp_method_get_from_reflection(method);
        Il2CppReflectionMethod* reflectionMethod = (Il2CppReflectionMethod*)method;
        return (IntPtr)reflectionMethod->method;
    }

    public static IntPtr il2cpp_method_get_from_reflection(IntPtr method) => _il2cpp_method_get_from_reflection(method);

    //public static IntPtr il2cpp_method_get_object(IntPtr method, IntPtr refclass) => throw new NotImplementedException();
    
    public static bool il2cpp_method_is_generic(IntPtr method) => throw new NotImplementedException();
    
    public static bool il2cpp_method_is_inflated(IntPtr method) => throw new NotImplementedException();
    
    public static bool il2cpp_method_is_instance(IntPtr method) => throw new NotImplementedException();
    
    public static uint il2cpp_method_get_param_count(IntPtr method) => throw new NotImplementedException();
    
    public static IntPtr il2cpp_method_get_param(IntPtr method, uint index) => throw new NotImplementedException();
    
    public static IntPtr il2cpp_method_get_class(IntPtr method) => throw new NotImplementedException();
    
    public static bool il2cpp_method_has_attribute(IntPtr method, IntPtr attr_class) => throw new NotImplementedException();
    
    public static uint il2cpp_method_get_flags(IntPtr method, ref uint iflags) => throw new NotImplementedException();
    
    //public static uint il2cpp_method_get_token(IntPtr method) => throw new NotImplementedException();
    
    public static IntPtr il2cpp_method_get_param_name(IntPtr method, uint index) => throw new NotImplementedException();
    
    public static void il2cpp_profiler_install(IntPtr prof, IntPtr shutdown_callback) => throw new NotImplementedException();
    
    public static void il2cpp_profiler_install_enter_leave(IntPtr enter, IntPtr fleave) => throw new NotImplementedException();
    
    public static void il2cpp_profiler_install_allocation(IntPtr callback) => throw new NotImplementedException();
    
    public static void il2cpp_profiler_install_gc(IntPtr callback, IntPtr heap_resize_callback) => throw new NotImplementedException();
    
    public static void il2cpp_profiler_install_fileio(IntPtr callback) => throw new NotImplementedException();
    
    public static void il2cpp_profiler_install_thread(IntPtr start, IntPtr end) => throw new NotImplementedException();
    
    public static uint il2cpp_property_get_flags(IntPtr prop) => throw new NotImplementedException();
    
    public static IntPtr il2cpp_property_get_get_method(IntPtr prop) => throw new NotImplementedException();
    
    public static IntPtr il2cpp_property_get_set_method(IntPtr prop) => throw new NotImplementedException();
    
    public static IntPtr il2cpp_property_get_name(IntPtr prop) => throw new NotImplementedException();
    
    public static IntPtr il2cpp_property_get_parent(IntPtr prop) => throw new NotImplementedException();
    
    //public static IntPtr il2cpp_object_get_class(IntPtr obj) => throw new NotImplementedException();
    
    public static uint il2cpp_object_get_size(IntPtr obj) => throw new NotImplementedException();
    
    //public static IntPtr il2cpp_object_get_virtual_method(IntPtr obj, IntPtr method) => throw new NotImplementedException();
    
    //public static IntPtr il2cpp_object_new(IntPtr klass) => throw new NotImplementedException();
    
    //public static IntPtr il2cpp_object_unbox(IntPtr obj) => throw new NotImplementedException();

    //public static IntPtr il2cpp_value_box(IntPtr klass, IntPtr data) => throw new NotImplementedException();
    
    public static void il2cpp_monitor_enter(IntPtr obj) => throw new NotImplementedException();
    
    public static bool il2cpp_monitor_try_enter(IntPtr obj, uint timeout) => throw new NotImplementedException();
    
    public static void il2cpp_monitor_exit(IntPtr obj) => throw new NotImplementedException();
    
    public static void il2cpp_monitor_pulse(IntPtr obj) => throw new NotImplementedException();
    
    public static void il2cpp_monitor_pulse_all(IntPtr obj) => throw new NotImplementedException();
    
    public static void il2cpp_monitor_wait(IntPtr obj) => throw new NotImplementedException();
    
    public static bool il2cpp_monitor_try_wait(IntPtr obj, uint timeout) => throw new NotImplementedException();
    
    //public static IntPtr il2cpp_runtime_invoke(IntPtr method, IntPtr obj, void** param, ref IntPtr exc) => throw new NotImplementedException();

    //public static IntPtr il2cpp_runtime_invoke_convert_args(IntPtr method, IntPtr obj, void** param,
    //    int paramCount, ref IntPtr exc) => throw new NotImplementedException();
    
    //public static void il2cpp_runtime_class_init(IntPtr klass) => throw new NotImplementedException();
    public static void il2cpp_runtime_object_init(IntPtr obj) => throw new NotImplementedException();
    
    public static void il2cpp_runtime_object_init_exception(IntPtr obj, ref IntPtr exc) => throw new NotImplementedException();
    
    //public static int il2cpp_string_length(IntPtr str) => throw new NotImplementedException();

    //public static char* il2cpp_string_chars(IntPtr str) => throw new NotImplementedException();

    //public static IntPtr il2cpp_string_new(string str) => throw new NotImplementedException();
    
    public static IntPtr il2cpp_string_new_len(string str, uint length) => throw new NotImplementedException();
    
    //public static IntPtr il2cpp_string_new_utf16(char* text, int len) => throw new NotImplementedException();
    
    public static IntPtr il2cpp_string_new_wrapper(string str) => throw new NotImplementedException();
    
    public static IntPtr il2cpp_string_intern(string str) => throw new NotImplementedException();
    
    public static IntPtr il2cpp_string_is_interned(string str) => throw new NotImplementedException();
    
    public static IntPtr il2cpp_thread_current() => throw new NotImplementedException();
    
    public static IntPtr il2cpp_thread_attach(IntPtr domain) => throw new NotImplementedException();
    
    public static void il2cpp_thread_detach(IntPtr thread) => throw new NotImplementedException();
    
    public static void** il2cpp_thread_get_all_attached_threads(ref uint size) => throw new NotImplementedException();
    
    public static bool il2cpp_is_vm_thread(IntPtr thread) => throw new NotImplementedException();
    
    public static void il2cpp_current_thread_walk_frame_stack(IntPtr func, IntPtr user_data) => throw new NotImplementedException();
    
    public static void il2cpp_thread_walk_frame_stack(IntPtr thread, IntPtr func, IntPtr user_data) => throw new NotImplementedException();
    
    public static bool il2cpp_current_thread_get_top_frame(IntPtr frame) => throw new NotImplementedException();
    
    public static bool il2cpp_thread_get_top_frame(IntPtr thread, IntPtr frame) => throw new NotImplementedException();
    
    public static bool il2cpp_current_thread_get_frame_at(int offset, IntPtr frame) => throw new NotImplementedException();
    
    public static bool il2cpp_thread_get_frame_at(IntPtr thread, int offset, IntPtr frame) => throw new NotImplementedException();
    
    public static int il2cpp_current_thread_get_stack_depth() => throw new NotImplementedException();
    
    public static int il2cpp_thread_get_stack_depth(IntPtr thread) => throw new NotImplementedException();
    
    public static IntPtr il2cpp_type_get_object(IntPtr type) => throw new NotImplementedException();
    
    public static int il2cpp_type_get_type(IntPtr type) => throw new NotImplementedException();
    
    public static IntPtr il2cpp_type_get_class_or_element_class(IntPtr type) => throw new NotImplementedException();
    
    public static IntPtr il2cpp_type_get_name(IntPtr type) => throw new NotImplementedException();
    
    public static bool il2cpp_type_is_byref(IntPtr type) => throw new NotImplementedException();
    
    public static uint il2cpp_type_get_attrs(IntPtr type) => throw new NotImplementedException();
    
    public static bool il2cpp_type_equals(IntPtr type, IntPtr otherType) => throw new NotImplementedException();
    
    public static IntPtr il2cpp_type_get_assembly_qualified_name(IntPtr type) => throw new NotImplementedException();
    
    public static IntPtr il2cpp_image_get_assembly(IntPtr image) => throw new NotImplementedException();
    
    //public static IntPtr il2cpp_image_get_name(IntPtr image) => throw new NotImplementedException();
    
    public static IntPtr il2cpp_image_get_filename(IntPtr image) => throw new NotImplementedException();
    
    public static IntPtr il2cpp_image_get_entry_point(IntPtr image) => throw new NotImplementedException();
    
    public static uint il2cpp_image_get_class_count(IntPtr image) => throw new NotImplementedException();
    
    public static IntPtr il2cpp_image_get_class(IntPtr image, uint index) => throw new NotImplementedException();
    
    public static IntPtr il2cpp_capture_memory_snapshot() => throw new NotImplementedException();
    
    public static void il2cpp_free_captured_memory_snapshot(IntPtr snapshot) => throw new NotImplementedException();
    
    public static void il2cpp_set_find_plugin_callback(IntPtr method) => throw new NotImplementedException();
    
    public static void il2cpp_register_log_callback(IntPtr method) => throw new NotImplementedException();
    
    public static void il2cpp_debugger_set_agent_options(IntPtr options) => throw new NotImplementedException();
    
    public static bool il2cpp_is_debugger_attached() => throw new NotImplementedException();
    
    public static void il2cpp_unity_install_unitytls_interface(void* unitytlsInterfaceStruct) => throw new NotImplementedException();
    
    public static IntPtr il2cpp_custom_attrs_from_class(IntPtr klass) => throw new NotImplementedException();
    
    public static IntPtr il2cpp_custom_attrs_from_method(IntPtr method) => throw new NotImplementedException();
    
    public static IntPtr il2cpp_custom_attrs_get_attr(IntPtr ainfo, IntPtr attr_klass) => throw new NotImplementedException();
    
    public static bool il2cpp_custom_attrs_has_attr(IntPtr ainfo, IntPtr attr_klass) => throw new NotImplementedException();
    
    public static IntPtr il2cpp_custom_attrs_construct(IntPtr cinfo) => throw new NotImplementedException();
    
    public static void il2cpp_custom_attrs_free(IntPtr ainfo) => throw new NotImplementedException();
    #endregion
}
