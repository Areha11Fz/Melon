using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Text;
using Il2CppInterop.Common;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppInterop.Runtime.InteropTypes.Fields;
using Il2CppInterop.Runtime.Runtime;
using Il2CppInterop.Runtime.Runtime.VersionSpecific.Class;
using Il2CppInterop.Runtime.Runtime.VersionSpecific.MethodInfo;
using Microsoft.Extensions.Logging;
using ValueType = Il2CppSystem.ValueType;
using Void = Il2CppSystem.Void;

namespace Il2CppInterop.Runtime.Injection;

public unsafe class Il2CppInterfaceCollection : List<INativeClassStruct>
{
    public Il2CppInterfaceCollection(IEnumerable<INativeClassStruct> interfaces) : base(interfaces)
    {
    }

    public Il2CppInterfaceCollection(IEnumerable<Type> interfaces) : base(ResolveNativeInterfaces(interfaces))
    {
    }

    private static IEnumerable<INativeClassStruct> ResolveNativeInterfaces(IEnumerable<Type> interfaces)
    {
        return interfaces.Select(it =>
        {
            var classPointer = Il2CppClassPointerStore.GetNativeClassPointer(it);
            if (classPointer == IntPtr.Zero)
                throw new ArgumentException(
                    $"Type {it} doesn't have an IL2CPP class pointer, which means it's not an IL2CPP interface");
            return UnityVersionHandler.Wrap((Il2CppClass*)classPointer);
        });
    }

    public static implicit operator Il2CppInterfaceCollection(INativeClassStruct[] interfaces)
    {
        return new(interfaces);
    }

    public static implicit operator Il2CppInterfaceCollection(Type[] interfaces)
    {
        return new(interfaces);
    }
}

public class RegisterTypeOptions
{
    public static readonly RegisterTypeOptions Default = new();

    public bool LogSuccess { get; init; } = true;
    public Func<Type, Type[]>? InterfacesResolver { get; init; } = null;
    public Il2CppInterfaceCollection? Interfaces { get; init; } = null;
}

public static unsafe partial class ClassInjector
{
    /// <summary> type.FullName </summary>
    private static readonly HashSet<string> InjectedTypes = new();

    /// <summary> (method) : (method_inst, method) </summary>
    private static readonly Dictionary<IntPtr, (MethodInfo, Dictionary<IntPtr, IntPtr>)>
        InflatedMethodFromContextDictionary = new();

    private static readonly ConcurrentDictionary<string, InvokerDelegate> InvokerCache = new();

    private static readonly ConcurrentDictionary<(Type type, FieldAttributes attrs), IntPtr>
        _injectedFieldTypes = new();

    private static readonly VoidCtorDelegate FinalizeDelegate = Finalize;

    public static void ProcessNewObject(Il2CppObjectBase obj)
    {
        var pointer = obj.Pointer;
        var handle = GCHandle.Alloc(obj, GCHandleType.Normal);
        AssignGcHandle(pointer, handle);
    }

    public static IntPtr DerivedConstructorPointer<T>()
    {
        return IL2CPP.il2cpp_object_new(Il2CppClassPointerStore<T>
            .NativeClassPtr); // todo: consider calling base constructor
    }

    public static void DerivedConstructorBody(Il2CppObjectBase objectBase)
    {
        if (objectBase.isWrapped)
            return;
        var fields = objectBase.GetType()
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Where(IsFieldEligible)
            .ToArray();
        foreach (var field in fields)
            field.SetValue(objectBase, field.FieldType.GetConstructor(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                    new[] { typeof(Il2CppObjectBase), typeof(string) }, Array.Empty<ParameterModifier>())
                .Invoke(new object[] { objectBase, field.Name })
            );
        var ownGcHandle = GCHandle.Alloc(objectBase, GCHandleType.Normal);
        AssignGcHandle(objectBase.Pointer, ownGcHandle);
    }

    public static void AssignGcHandle(IntPtr pointer, GCHandle gcHandle)
    {
        var handleAsPointer = GCHandle.ToIntPtr(gcHandle);
        if (pointer == IntPtr.Zero) throw new NullReferenceException(nameof(pointer));
        ClassInjectorBase.GetInjectedData(pointer)->managedGcHandle = GCHandle.ToIntPtr(gcHandle);
    }


    public static bool IsTypeRegisteredInIl2Cpp<T>() where T : class
    {
        return IsTypeRegisteredInIl2Cpp(typeof(T));
    }

    public static bool IsTypeRegisteredInIl2Cpp(Type type)
    {
        var currentPointer = Il2CppClassPointerStore.GetNativeClassPointer(type);
        if (currentPointer != IntPtr.Zero)
            return true;
        lock (InjectedTypes)
        {
            if (InjectedTypes.Contains(type.FullName))
                return true;
        }

        return false;
    }

    public static void RegisterTypeInIl2Cpp<T>() where T : class
    {
        RegisterTypeInIl2Cpp(typeof(T));
    }

    public static void RegisterTypeInIl2Cpp(Type type)
    {
        RegisterTypeInIl2Cpp(type, RegisterTypeOptions.Default);
    }

    public static void RegisterTypeInIl2Cpp<T>(RegisterTypeOptions options) where T : class
    {
        RegisterTypeInIl2Cpp(typeof(T), options);
    }

    private static Il2CppMethodInfo* CopyMethod(Il2CppMethodInfo* orig)
    {
        var newmethod = UnityVersionHandler.NewMethod();
        Buffer.MemoryCopy(orig, newmethod.MethodInfoPointer, 200, 200);
        return newmethod.MethodInfoPointer;
    }

    private static KeyValuePair<DelegateSupport.MethodSignature, IntPtr> MapMethodSig(MethodInfo proxy) =>
        KeyValuePair.Create(new DelegateSupport.MethodSignature(proxy, false), InjectorHelpers.GetIl2CppMethodInfoPointer(proxy));

    private static IDictionary<DelegateSupport.MethodSignature, IntPtr> s_methodHijacks;
    private static void SetupHijacks()
    {
        if (s_methodHijacks == null)
        {
            s_methodHijacks = new Dictionary<DelegateSupport.MethodSignature, IntPtr>();

            var flags = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            s_methodHijacks.Add(MapMethodSig(typeof(Il2CppSystem.Array.ArrayEnumerator).GetMethod("MoveNext", flags)));
            s_methodHijacks.Add(MapMethodSig(typeof(Il2CppSystem.Array.ArrayEnumerator).GetMethod("get_Current", flags)));
            s_methodHijacks.Add(MapMethodSig(typeof(MiHoYo.FBIK.FBIKTest).GetMethod("Start", flags)));
            s_methodHijacks.Add(MapMethodSig(typeof(MiHoYo.FBIK.FBIKTest).GetMethod("OnAnimatorIK", flags)));

            foreach (var m in s_methodHijacks)
                IL2CPP.il2cpp_method_get_name(m.Value); // ensure initialized
        }
    }

    public static void RegisterTypeInIl2Cpp(Type type, RegisterTypeOptions options)
    {
        var interfaces = options.Interfaces;
        if (interfaces == null)
        {
            var interfacesAttribute = type.GetCustomAttribute<Il2CppImplementsAttribute>();
            interfaces = interfacesAttribute?.Interfaces ??
                         options.InterfacesResolver?.Invoke(type) ?? Array.Empty<Type>();
        }

        if (type == null)
            throw new ArgumentException("Type argument cannot be null");

        if (type.IsGenericType || type.IsGenericTypeDefinition)
            throw new ArgumentException($"Type {type} is generic and can't be used in il2cpp");

        var currentPointer = Il2CppClassPointerStore.GetNativeClassPointer(type);
        if (currentPointer != IntPtr.Zero)
            return; //already registered in il2cpp

        var baseType = type.BaseType;
        if (baseType == null)
            throw new ArgumentException($"Class {type} does not inherit from a class registered in il2cpp");

        INativeClassStruct klass = null;
        if (baseType == typeof(Il2CppSystem.Object))
        {
            klass = UnityVersionHandler.Wrap((Il2CppClass*)Il2CppClassPointerStore.GetNativeClassPointer(typeof(Il2CppSystem.Array.ArrayEnumerator)));
        }
        else if (baseType == typeof(UnityEngine.MonoBehaviour))
        {
            klass = UnityVersionHandler.Wrap((Il2CppClass*)Il2CppClassPointerStore.GetNativeClassPointer(typeof(MiHoYo.FBIK.FBIKTest)));
        }
        else
        {
            Logger.Instance.LogWarning("[GI Warning] Injecting class {name} unsupported", type.FullName);
            return;
        }

        lock (InjectedTypes)
        {
            if (!InjectedTypes.Add(type.FullName))
                throw new ArgumentException(
                    $"Type with FullName {type.FullName} is already injected. Don't inject the same type twice, or use a different namespace");
        }

        var methods_list = klass.Methods;
        var method_count = klass.MethodCount;

        List<INativeMethodInfoStruct> new_methods_list = new();
        List<VirtualInvokeData> new_vtable = new();

        var vtable = (VirtualInvokeData*)klass.VTable;
        for (var i = 0; i < klass.VtableCount; i++)
            new_vtable.Add(vtable[i]);

        var newctor = UnityVersionHandler.Wrap(CopyMethod(methods_list[0]));
        newctor.MethodPointer = Marshal.GetFunctionPointerForDelegate(CreateEmptyCtor(type, Array.Empty<FieldInfo>()));
        new_methods_list.Add(newctor);

        SetupHijacks();
        foreach (var m in type.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            var sig = new DelegateSupport.MethodSignature(m, false);
            if (!s_methodHijacks.ContainsKey(sig) || !IsMethodEligible(m))
                continue;

            var newmethod = UnityVersionHandler.Wrap(CopyMethod((Il2CppMethodInfo*)s_methodHijacks[sig]));
            newmethod.MethodPointer = Marshal.GetFunctionPointerForDelegate(GetOrCreateTrampoline(m));

            var slot = newmethod.Slot;
            if (slot != ushort.MaxValue && slot < new_vtable.Count)
            {
                var v = new_vtable[slot];
                v.method = newmethod.MethodInfoPointer;
                v.methodPtr = newmethod.MethodPointer;
                new_vtable[slot] = v;
            }

            var newex = Marshal.AllocHGlobal(24);
            Buffer.MemoryCopy(newmethod.Extra.ToPointer(), newex.ToPointer(), 24, 24);
            newmethod.Extra = newex;
            newmethod.Name = (IntPtr)((ulong)Marshal.StringToHGlobalAnsi(m.Name) ^ 0x4D39E3CF64E89510);
            new_methods_list.Add(newmethod);
        }

        InjectorHelpers.Setup();
        
        var size = klass.VtableCount * sizeof(VirtualInvokeData) + 328;
        var newklass = UnityVersionHandler.NewClass(klass.VtableCount);
        var list = (Il2CppMethodInfo**)Marshal.AllocHGlobal(new_methods_list.Count * sizeof(IntPtr));
        Buffer.MemoryCopy(klass.ClassPointer, newklass.ClassPointer, size, size);

        for (var i = 0; i < new_methods_list.Count; i++)
            list[i] = new_methods_list[i].MethodInfoPointer;

        var vt = (VirtualInvokeData*)newklass.VTable;
        for (var i = 0; i < klass.VtableCount; i++)
            vt[i] = new_vtable[i];

        newklass.Methods = list;
        newklass.MethodCount = (ushort)new_methods_list.Count;
        newklass.ByValArg.Data = (IntPtr)InjectorHelpers.CreateClassToken(newklass.Pointer);
        newklass.InstanceSize += (uint)sizeof(InjectedClassData);

        Il2CppClassPointerStore.SetNativeClassPointer(type, newklass.Pointer);
        RuntimeSpecificsStore.SetClassInfo(newklass.Pointer, true);
        InjectorHelpers.AddTypeToLookup(type, newklass.Pointer);
        return;

        var baseClassPointer =
            UnityVersionHandler.Wrap((Il2CppClass*)Il2CppClassPointerStore.GetNativeClassPointer(baseType));
        if (baseClassPointer == null)
        {
            RegisterTypeInIl2Cpp(baseType, new RegisterTypeOptions { LogSuccess = options.LogSuccess });
            baseClassPointer =
                UnityVersionHandler.Wrap((Il2CppClass*)Il2CppClassPointerStore.GetNativeClassPointer(baseType));
        }

        InjectorHelpers.Setup();

        // Initialize the vtable of all base types (Class::Init is recursive internally)
        InjectorHelpers.ClassInit(baseClassPointer.ClassPointer);

        if (baseClassPointer.ValueType || baseClassPointer.EnumType)
            throw new ArgumentException($"Base class {baseType} is value type and can't be inherited from");

        if (baseClassPointer.IsGeneric)
            throw new ArgumentException($"Base class {baseType} is generic and can't be inherited from");

        if ((baseClassPointer.Flags & Il2CppClassAttributes.TYPE_ATTRIBUTE_SEALED) != 0)
            throw new ArgumentException($"Base class {baseType} is sealed and can't be inherited from");

        if ((baseClassPointer.Flags & Il2CppClassAttributes.TYPE_ATTRIBUTE_INTERFACE) != 0)
            throw new ArgumentException($"Base class {baseType} is an interface and can't be inherited from");

        if (interfaces.Any(i => (i.Flags & Il2CppClassAttributes.TYPE_ATTRIBUTE_INTERFACE) == 0))
            throw new ArgumentException($"Some of the interfaces in {interfaces} are not interfaces");

        lock (InjectedTypes)
        {
            if (!InjectedTypes.Add(type.FullName))
                throw new ArgumentException(
                    $"Type with FullName {type.FullName} is already injected. Don't inject the same type twice, or use a different namespace");
        }

        var interfaceFunctionCount = interfaces.Sum(i => i.MethodCount);
        var classPointer = UnityVersionHandler.NewClass(baseClassPointer.VtableCount + interfaceFunctionCount);

        classPointer.Image = InjectorHelpers.InjectedImage.ImagePointer;
        classPointer.Parent = baseClassPointer.ClassPointer;
        classPointer.ElementClass = classPointer.Class = classPointer.CastClass = classPointer.ClassPointer;
        classPointer.NativeSize = -1;
        classPointer.ActualSize = classPointer.InstanceSize = baseClassPointer.InstanceSize;

        classPointer.Initialized = true;
        classPointer.InitializedAndNoError = true;
        classPointer.SizeInited = true;
        classPointer.HasFinalize = true;
        classPointer.IsVtableInitialized = true;

        classPointer.Name = Marshal.StringToHGlobalAnsi(type.Name);
        classPointer.Namespace = Marshal.StringToHGlobalAnsi(type.Namespace ?? string.Empty);

        classPointer.ThisArg.Type = classPointer.ByValArg.Type = Il2CppTypeEnum.IL2CPP_TYPE_CLASS;
        classPointer.ThisArg.ByRef = true;

        classPointer.Flags = baseClassPointer.Flags; // todo: adjust flags?

        if (!type.IsAbstract) classPointer.Flags &= ~Il2CppClassAttributes.TYPE_ATTRIBUTE_ABSTRACT;

        var fieldsToInject = type
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Where(IsFieldEligible)
            .ToArray();
        classPointer.FieldCount = (ushort)fieldsToInject.Length;

        var il2cppFields =
            (Il2CppFieldInfo*)Marshal.AllocHGlobal(classPointer.FieldCount * UnityVersionHandler.FieldInfoSize());
        var fieldOffset = (int)classPointer.InstanceSize;
        for (var i = 0; i < classPointer.FieldCount; i++)
        {
            var fieldInfo = UnityVersionHandler.Wrap(il2cppFields + i * UnityVersionHandler.FieldInfoSize());
            fieldInfo.Name = Marshal.StringToHGlobalAnsi(fieldsToInject[i].Name);
            fieldInfo.Parent = classPointer.ClassPointer;
            fieldInfo.Offset = fieldOffset;

            var fieldType = fieldsToInject[i].FieldType == typeof(Il2CppStringField)
                ? typeof(string)
                : fieldsToInject[i].FieldType.GenericTypeArguments[0];
            var fieldAttributes = fieldsToInject[i].Attributes;
            var fieldInfoClass = Il2CppClassPointerStore.GetNativeClassPointer(fieldType);
            if (!_injectedFieldTypes.TryGetValue((fieldType, fieldAttributes), out var fieldTypePtr))
            {
                var classType =
                    UnityVersionHandler.Wrap((Il2CppTypeStruct*)IL2CPP.il2cpp_class_get_type(fieldInfoClass));

                var duplicatedType = UnityVersionHandler.NewType();
                duplicatedType.Data = classType.Data;
                duplicatedType.Attrs = (ushort)fieldAttributes;
                duplicatedType.Type = classType.Type;
                duplicatedType.ByRef = classType.ByRef;
                duplicatedType.Pinned = classType.Pinned;

                _injectedFieldTypes[(fieldType, fieldAttributes)] = duplicatedType.Pointer;
                fieldTypePtr = duplicatedType.Pointer;
            }

            fieldInfo.Type = (Il2CppTypeStruct*)fieldTypePtr;
            if (fieldInfoClass == IntPtr.Zero)
                throw new Exception($"Type {fieldType} in {type}.{fieldsToInject[i].Name} doesn't exist in Il2Cpp");

            if (IL2CPP.il2cpp_class_is_valuetype(fieldInfoClass))
            {
                uint _align = 0;
                var fieldSize = IL2CPP.il2cpp_class_value_size(fieldInfoClass, ref _align);
                fieldOffset += fieldSize;
            }
            else
            {
                fieldOffset += sizeof(Il2CppObject*);
            }
        }

        classPointer.Fields = il2cppFields;

        classPointer.InstanceSize = (uint)(fieldOffset + sizeof(InjectedClassData));
        classPointer.ActualSize = classPointer.InstanceSize;

        var eligibleMethods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Where(IsMethodEligible).ToArray();
        var methodsOffset = type.IsAbstract ? 1 : 2; // 1 is the finalizer, 1 is empty ctor
        var methodCount = methodsOffset + eligibleMethods.Length;

        classPointer.MethodCount = (ushort)methodCount;
        var methodPointerArray = (Il2CppMethodInfo**)Marshal.AllocHGlobal(methodCount * IntPtr.Size);
        classPointer.Methods = methodPointerArray;

        methodPointerArray[0] = ConvertStaticMethod(FinalizeDelegate, "Finalize", classPointer);
        var finalizeMethod = UnityVersionHandler.Wrap(methodPointerArray[0]);
        if (!type.IsAbstract) methodPointerArray[1] = ConvertStaticMethod(CreateEmptyCtor(type, fieldsToInject), ".ctor", classPointer);
        var infos = new Dictionary<(string, int, bool), int>(eligibleMethods.Length);
        for (var i = 0; i < eligibleMethods.Length; i++)
        {
            var methodInfo = eligibleMethods[i];
            var methodInfoPointer = methodPointerArray[i + methodsOffset] = ConvertMethodInfo(methodInfo, classPointer);
            if (methodInfo.IsGenericMethod && !methodInfo.IsAbstract)
                InflatedMethodFromContextDictionary.Add((IntPtr)methodInfoPointer, (methodInfo, new Dictionary<IntPtr, IntPtr>()));
            infos[(methodInfo.Name, methodInfo.GetParameters().Length, methodInfo.IsGenericMethod)] = i + methodsOffset;
        }

        var abstractMethods = eligibleMethods.Where(x => x.IsAbstract).ToArray();

        var vTablePointer = (VirtualInvokeData*)classPointer.VTable;
        var baseVTablePointer = (VirtualInvokeData*)baseClassPointer.VTable;
        classPointer.VtableCount = (ushort)(baseClassPointer.VtableCount + interfaceFunctionCount + abstractMethods.Length);

        var extendsAbstract = baseClassPointer.Flags.HasFlag(Il2CppClassAttributes.TYPE_ATTRIBUTE_ABSTRACT);
        var abstractBaseMethods = new List<INativeMethodInfoStruct>();

        if (extendsAbstract)
        {
            static void FindAbstractMethods(List<INativeMethodInfoStruct> list, INativeClassStruct klass)
            {
                if (klass.Parent != default) FindAbstractMethods(list, UnityVersionHandler.Wrap(klass.Parent));

                for (var i = 0; i < klass.MethodCount; i++)
                {
                    var baseMethod = UnityVersionHandler.Wrap(klass.Methods[i]);
                    var name = Marshal.PtrToStringAnsi(baseMethod.Name)!;

                    if (baseMethod.Flags.HasFlag(Il2CppMethodFlags.METHOD_ATTRIBUTE_ABSTRACT))
                    {
                        list.Add(baseMethod);
                    }
                    else
                    {
                        var existing = list.SingleOrDefault(m =>
                        {
                            if (Marshal.PtrToStringAnsi(m.Name) != name) return false;
                            if (m.ParametersCount != baseMethod.ParametersCount) return false;

                            for (var i = 0; i < m.ParametersCount; i++)
                            {
                                var parameterInfo = UnityVersionHandler.Wrap(baseMethod.Parameters, i);
                                var otherParameterInfo = UnityVersionHandler.Wrap(m.Parameters, i);

                                if (Marshal.PtrToStringAnsi(parameterInfo.Name) != Marshal.PtrToStringAnsi(otherParameterInfo.Name)) return false;

                                if (GetIl2CppTypeFullName(parameterInfo.ParameterType) != GetIl2CppTypeFullName(otherParameterInfo.ParameterType)) return false;
                            }

                            return true;
                        });

                        if (existing != null)
                        {
                            list.Remove(existing);
                        }
                    }
                }
            }

            FindAbstractMethods(abstractBaseMethods, baseClassPointer);
        }

        var abstractV = 0;
        for (var i = 0; i < baseClassPointer.VtableCount; i++)
        {
            vTablePointer[i] = baseVTablePointer[i];

            INativeMethodInfoStruct baseMethod;

            if (baseVTablePointer[i].method == default)
            {
                if (!extendsAbstract) throw new NullReferenceException("VTable method was null even though base type isn't abstract");

                baseMethod = abstractBaseMethods[abstractV++];

                vTablePointer[i].method = baseMethod.MethodInfoPointer;
                vTablePointer[i].methodPtr = baseMethod.MethodPointer;
            }
            else
            {
                baseMethod = UnityVersionHandler.Wrap(vTablePointer[i].method);
            }

            var methodName = Marshal.PtrToStringAnsi(baseMethod.Name);

            if (methodName == "Finalize") // slot number is not static
            {
                vTablePointer[i].method = methodPointerArray[0];
                vTablePointer[i].methodPtr = finalizeMethod.MethodPointer;
                continue;
            }

            var parameters = new Type[baseMethod.ParametersCount];

            for (var j = 0; j < baseMethod.ParametersCount; j++)
            {
                var parameterInfo = UnityVersionHandler.Wrap(baseMethod.Parameters, j);
                var parameterType = SystemTypeFromIl2CppType(parameterInfo.ParameterType);

                parameters[j] = parameterType;
            }

            var monoMethodImplementation = type.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, parameters);

            if (monoMethodImplementation != null && monoMethodImplementation.IsAbstract)
            {
                continue;
            }

            var methodPointerArrayIndex = Array.IndexOf(eligibleMethods, monoMethodImplementation);
            if (methodPointerArrayIndex >= 0)
            {
                var method = UnityVersionHandler.Wrap(methodPointerArray[methodPointerArrayIndex + methodsOffset]);
                vTablePointer[i].method = methodPointerArray[methodPointerArrayIndex + methodsOffset];
                vTablePointer[i].methodPtr = method.MethodPointer;
            }

            if (vTablePointer[i].method == default || vTablePointer[i].methodPtr == IntPtr.Zero)
            {
                throw new Exception("No method found for vtable entry " + methodName);
            }
        }

        var offsets = new int[interfaces.Count];

        var index = baseClassPointer.VtableCount;
        for (var i = 0; i < interfaces.Count; i++)
        {
            offsets[i] = index;
            for (var j = 0; j < interfaces[i].MethodCount; j++)
            {
                var vTableMethod = UnityVersionHandler.Wrap(interfaces[i].Methods[j]);
                var methodName = Marshal.PtrToStringAnsi(vTableMethod.Name);
                if (!infos.TryGetValue((methodName, vTableMethod.ParametersCount, vTableMethod.IsGeneric),
                        out var methodIndex))
                {
                    ++index;
                    continue;
                }

                var method = methodPointerArray[methodIndex];
                vTablePointer[index].method = method;
                vTablePointer[index].methodPtr = UnityVersionHandler.Wrap(method).MethodPointer;
                ++index;
            }
        }

        var interfaceCount = baseClassPointer.InterfaceCount + interfaces.Count;
        classPointer.InterfaceCount = (ushort)interfaceCount;
        classPointer.ImplementedInterfaces = (Il2CppClass**)Marshal.AllocHGlobal(interfaceCount * IntPtr.Size);
        for (var i = 0; i < baseClassPointer.InterfaceCount; i++)
            classPointer.ImplementedInterfaces[i] = baseClassPointer.ImplementedInterfaces[i];
        for (int i = baseClassPointer.InterfaceCount; i < interfaceCount; i++)
            classPointer.ImplementedInterfaces[i] = interfaces[i - baseClassPointer.InterfaceCount].ClassPointer;

        var interfaceOffsetsCount = baseClassPointer.InterfaceOffsetsCount + interfaces.Count;
        classPointer.InterfaceOffsetsCount = (ushort)interfaceOffsetsCount;
        classPointer.InterfaceOffsets =
            (Il2CppRuntimeInterfaceOffsetPair*)Marshal.AllocHGlobal(interfaceOffsetsCount *
                                                                     Marshal
                                                                         .SizeOf<Il2CppRuntimeInterfaceOffsetPair>());
        for (var i = 0; i < baseClassPointer.InterfaceOffsetsCount; i++)
            classPointer.InterfaceOffsets[i] = baseClassPointer.InterfaceOffsets[i];
        for (int i = baseClassPointer.InterfaceOffsetsCount; i < interfaceOffsetsCount; i++)
            classPointer.InterfaceOffsets[i] = new Il2CppRuntimeInterfaceOffsetPair
            {
                interfaceType = interfaces[i - baseClassPointer.InterfaceOffsetsCount].ClassPointer,
                offset = offsets[i - baseClassPointer.InterfaceOffsetsCount]
            };

        for (var i = 0; i < abstractMethods.Length; i++)
        {
            vTablePointer[index++] = default;
        }

        var TypeHierarchyDepth = 1 + baseClassPointer.TypeHierarchyDepth;
        classPointer.TypeHierarchyDepth = (byte)TypeHierarchyDepth;
        classPointer.TypeHierarchy = (Il2CppClass**)Marshal.AllocHGlobal(TypeHierarchyDepth * IntPtr.Size);
        for (var i = 0; i < TypeHierarchyDepth; i++)
            classPointer.TypeHierarchy[i] = baseClassPointer.TypeHierarchy[i];
        classPointer.TypeHierarchy[TypeHierarchyDepth - 1] = classPointer.ClassPointer;

        classPointer.ByValArg.Data =
            classPointer.ThisArg.Data = (IntPtr)InjectorHelpers.CreateClassToken(classPointer.Pointer);

        RuntimeSpecificsStore.SetClassInfo(classPointer.Pointer, true);
        Il2CppClassPointerStore.SetNativeClassPointer(type, classPointer.Pointer);

        InjectorHelpers.AddTypeToLookup(type, classPointer.Pointer);

        if (options.LogSuccess)
            Logger.Instance.LogInformation("Registered mono type {Type} in il2cpp domain", type);
    }

    private static bool IsTypeSupported(Type type)
    {
        if (type.IsValueType ||
            type == typeof(string) ||
            type.IsGenericParameter) return true;
        if (type.IsByRef) return IsTypeSupported(type.GetElementType());

        return typeof(Il2CppObjectBase).IsAssignableFrom(type);
    }

    private static bool IsFieldEligible(FieldInfo field)
    {
        if (!field.FieldType.IsGenericType) return field.FieldType == typeof(Il2CppStringField);
        var genericTypeDef = field.FieldType.GetGenericTypeDefinition();
        if (genericTypeDef != typeof(Il2CppReferenceField<>) && genericTypeDef != typeof(Il2CppValueField<>))
            return false;

        return IsTypeSupported(field.FieldType.GenericTypeArguments[0]);
    }

    private static bool IsMethodEligible(MethodInfo method)
    {
        if (method.Name == "Finalize") return false;
        if (method.IsStatic) return false;
        if (method.CustomAttributes.Any(it => typeof(HideFromIl2CppAttribute).IsAssignableFrom(it.AttributeType)))
            return false;

        if (method.DeclaringType != null)
        {
            if (method.DeclaringType.GetProperties(BindingFlags.Instance | BindingFlags.Public |
                                                   BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(property => property.GetAccessors(true).Contains(method))
                .Any(property =>
                    property.CustomAttributes.Any(it =>
                        typeof(HideFromIl2CppAttribute).IsAssignableFrom(it.AttributeType)))
               )
                return false;

            foreach (var eventInfo in method.DeclaringType.GetEvents(BindingFlags.Instance | BindingFlags.Public |
                                                                     BindingFlags.NonPublic |
                                                                     BindingFlags.DeclaredOnly))
                if ((eventInfo.GetAddMethod(true) == method || eventInfo.GetRemoveMethod(true) == method) &&
                    eventInfo.GetCustomAttribute<HideFromIl2CppAttribute>() != null)
                    return false;
        }

        if (!IsTypeSupported(method.ReturnType))
        {
            Logger.Instance.LogWarning(
                "Method {Method} on type {DeclaringType} has unsupported return type {ReturnType}", method.ToString(), method.DeclaringType, method.ReturnType);
            return false;
        }

        foreach (var parameter in method.GetParameters())
        {
            var parameterType = parameter.ParameterType;
            if (!IsTypeSupported(parameterType))
            {
                Logger.Instance.LogWarning(
                    "Method {Method} on type {DeclaringType} has unsupported parameter {Parameter} of type {ParameterType}", method.ToString(), method.DeclaringType, parameter, parameterType);
                return false;
            }
        }

        return true;
    }

    private static Il2CppMethodInfo* ConvertStaticMethod(VoidCtorDelegate voidCtor, string methodName,
        INativeClassStruct declaringClass)
    {
        var converted = UnityVersionHandler.NewMethod();
        converted.Name = Marshal.StringToHGlobalAnsi(methodName);
        converted.Class = declaringClass.ClassPointer;

        var invoker = new InvokerDelegate(StaticVoidIntPtrInvoker);
        GCHandle.Alloc(invoker);
        converted.InvokerMethod = Marshal.GetFunctionPointerForDelegate(invoker);

        converted.MethodPointer = Marshal.GetFunctionPointerForDelegate(voidCtor);
        converted.Slot = ushort.MaxValue;
        converted.ReturnType =
            (Il2CppTypeStruct*)IL2CPP.il2cpp_class_get_type(Il2CppClassPointerStore<Void>.NativeClassPtr);

        converted.Flags = Il2CppMethodFlags.METHOD_ATTRIBUTE_PUBLIC |
                          Il2CppMethodFlags.METHOD_ATTRIBUTE_HIDE_BY_SIG |
                          Il2CppMethodFlags.METHOD_ATTRIBUTE_SPECIAL_NAME |
                          Il2CppMethodFlags.METHOD_ATTRIBUTE_RT_SPECIAL_NAME;

        return converted.MethodInfoPointer;
    }

    private static Il2CppMethodInfo* ConvertMethodInfo(MethodInfo monoMethod, INativeClassStruct declaringClass)
    {
        var converted = UnityVersionHandler.NewMethod();
        converted.Name = Marshal.StringToHGlobalAnsi(monoMethod.Name);
        converted.Class = declaringClass.ClassPointer;

        var parameters = monoMethod.GetParameters();
        if (parameters.Length > 0)
        {
            converted.ParametersCount = (byte)parameters.Length;
            var paramsArray = UnityVersionHandler.NewMethodParameterArray(parameters.Length);
            converted.Parameters = paramsArray[0];
            for (var i = 0; i < parameters.Length; i++)
            {
                var parameterInfo = parameters[i];
                var param = UnityVersionHandler.Wrap(paramsArray[i]);
                if (UnityVersionHandler.ParameterInfoHasNamePosToken())
                {
                    param.Name = Marshal.StringToHGlobalAnsi(parameterInfo.Name);
                    param.Position = i;
                    param.Token = 0;
                }

                var parameterType = parameterInfo.ParameterType;
                if (!parameterType.IsGenericParameter)
                {
                    if (parameterType.IsByRef)
                    {
                        var elementType = parameterType.GetElementType();
                        if (!elementType.IsGenericParameter)
                        {
                            var elemType = UnityVersionHandler.Wrap(
                                (Il2CppTypeStruct*)IL2CPP.il2cpp_class_get_type(
                                    Il2CppClassPointerStore.GetNativeClassPointer(elementType)));
                            var refType = UnityVersionHandler.NewType();
                            refType.Data = elemType.Data;
                            refType.Attrs = elemType.Attrs;
                            refType.Type = elemType.Type;
                            refType.ByRef = true;
                            refType.Pinned = elemType.Pinned;
                            param.ParameterType = refType.TypePointer;
                        }
                        else
                        {
                            var type = UnityVersionHandler.NewType();
                            type.Type = Il2CppTypeEnum.IL2CPP_TYPE_MVAR;
                            type.ByRef = true;
                            param.ParameterType = type.TypePointer;
                        }
                    }
                    else
                    {
                        param.ParameterType =
                            (Il2CppTypeStruct*)IL2CPP.il2cpp_class_get_type(
                                Il2CppClassPointerStore.GetNativeClassPointer(parameterType));
                    }
                }
                else
                {
                    var type = UnityVersionHandler.NewType();
                    type.Type = Il2CppTypeEnum.IL2CPP_TYPE_MVAR;
                    param.ParameterType = type.TypePointer;
                }
            }
        }

        if (monoMethod.IsGenericMethod)
        {
            if (monoMethod.ContainsGenericParameters)
                converted.IsGeneric = true;
            else
                converted.IsInflated = true;
        }

        if (!monoMethod.ContainsGenericParameters && !monoMethod.IsAbstract)
        {
            converted.InvokerMethod = Marshal.GetFunctionPointerForDelegate(GetOrCreateInvoker(monoMethod));
            converted.MethodPointer = Marshal.GetFunctionPointerForDelegate(GetOrCreateTrampoline(monoMethod));
        }

        converted.Slot = ushort.MaxValue;

        if (!monoMethod.ReturnType.IsGenericParameter)
        {
            converted.ReturnType =
                (Il2CppTypeStruct*)IL2CPP.il2cpp_class_get_type(
                    Il2CppClassPointerStore.GetNativeClassPointer(monoMethod.ReturnType));
        }
        else
        {
            var type = UnityVersionHandler.NewType();
            type.Type = Il2CppTypeEnum.IL2CPP_TYPE_MVAR;
            converted.ReturnType = type.TypePointer;
        }

        converted.Flags = Il2CppMethodFlags.METHOD_ATTRIBUTE_PUBLIC |
                          Il2CppMethodFlags.METHOD_ATTRIBUTE_HIDE_BY_SIG;

        if (monoMethod.IsAbstract)
        {
            converted.Flags |= Il2CppMethodFlags.METHOD_ATTRIBUTE_ABSTRACT;
        }

        return converted.MethodInfoPointer;
    }

    private static VoidCtorDelegate CreateEmptyCtor(Type targetType, FieldInfo[] fieldsToInitialize)
    {
        var method = new DynamicMethod("FromIl2CppCtorDelegate", MethodAttributes.Public | MethodAttributes.Static,
            CallingConventions.Standard, typeof(void), new[] { typeof(IntPtr) }, targetType, true);

        var body = method.GetILGenerator();

        var monoCtor = targetType.GetConstructor(new[] { typeof(IntPtr) });
        if (monoCtor != null)
        {
            body.Emit(OpCodes.Ldarg_0);
            body.Emit(OpCodes.Newobj, monoCtor);
        }
        else
        {
            var local = body.DeclareLocal(targetType);
            body.Emit(OpCodes.Ldtoken, targetType);
            body.Emit(OpCodes.Call,
                typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle), BindingFlags.Public | BindingFlags.Static)!);
            body.Emit(OpCodes.Call,
                typeof(FormatterServices).GetMethod(nameof(FormatterServices.GetUninitializedObject),
                    BindingFlags.Public | BindingFlags.Static)!);
            body.Emit(OpCodes.Stloc, local);
            body.Emit(OpCodes.Ldloc, local);
            body.Emit(OpCodes.Ldarg_0);
            body.Emit(OpCodes.Call,
                typeof(Il2CppObjectBase).GetMethod(nameof(Il2CppObjectBase.CreateGCHandle),
                    BindingFlags.NonPublic | BindingFlags.Instance)!);
            body.Emit(OpCodes.Ldloc, local);
            body.Emit(OpCodes.Ldc_I4_1);
            body.Emit(OpCodes.Stfld,
                typeof(Il2CppObjectBase).GetField(nameof(Il2CppObjectBase.isWrapped),
                    BindingFlags.NonPublic | BindingFlags.Instance)!);
            body.Emit(OpCodes.Ldloc, local);
            body.Emit(OpCodes.Call,
                targetType.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                    Type.EmptyTypes, Array.Empty<ParameterModifier>())!);
            body.Emit(OpCodes.Ldloc, local);
        }

        foreach (var field in fieldsToInitialize)
        {
            body.Emit(OpCodes.Dup);
            body.Emit(OpCodes.Dup);
            body.Emit(OpCodes.Ldstr, field.Name);
            body.Emit(OpCodes.Newobj, field.FieldType.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                new[] { typeof(Il2CppObjectBase), typeof(string) }, Array.Empty<ParameterModifier>())
            );
            body.Emit(OpCodes.Stfld, field);
        }

        body.Emit(OpCodes.Call, typeof(ClassInjector).GetMethod(nameof(ProcessNewObject))!);

        body.Emit(OpCodes.Ret);

        var @delegate = (VoidCtorDelegate)method.CreateDelegate(typeof(VoidCtorDelegate));
        GCHandle.Alloc(@delegate); // pin it forever
        return @delegate;
    }

    public static void Finalize(IntPtr ptr)
    {
        var gcHandle = ClassInjectorBase.GetGcHandlePtrFromIl2CppObject(ptr);
        GCHandle.FromIntPtr(gcHandle).Free();
    }

    private static InvokerDelegate GetOrCreateInvoker(MethodInfo monoMethod)
    {
        return InvokerCache.GetOrAdd(ExtractSignature(monoMethod),
            (_, monoMethodInner) => CreateInvoker(monoMethodInner), monoMethod);
    }

    private static Delegate GetOrCreateTrampoline(MethodInfo monoMethod)
    {
        return CreateTrampoline(monoMethod);
    }

    private static InvokerDelegate CreateInvoker(MethodInfo monoMethod)
    {
        var parameterTypes = new[] { typeof(IntPtr), typeof(Il2CppMethodInfo*), typeof(IntPtr), typeof(IntPtr*) };

        var method = new DynamicMethod("Invoker_" + ExtractSignature(monoMethod),
            MethodAttributes.Static | MethodAttributes.Public, CallingConventions.Standard, typeof(IntPtr),
            parameterTypes, monoMethod.DeclaringType, true);

        var body = method.GetILGenerator();

        body.Emit(OpCodes.Ldarg_2);
        for (var i = 0; i < monoMethod.GetParameters().Length; i++)
        {
            var parameterInfo = monoMethod.GetParameters()[i];
            body.Emit(OpCodes.Ldarg_3);
            body.Emit(OpCodes.Ldc_I4, i * IntPtr.Size);
            body.Emit(OpCodes.Add_Ovf_Un);
            var nativeType = parameterInfo.ParameterType.NativeType();
            body.Emit(OpCodes.Ldobj, typeof(IntPtr));
            if (nativeType != typeof(IntPtr))
                body.Emit(OpCodes.Ldobj, nativeType);
        }

        body.Emit(OpCodes.Ldarg_0);
        body.EmitCalli(OpCodes.Calli, CallingConvention.Cdecl, monoMethod.ReturnType.NativeType(),
            new[] { typeof(IntPtr) }.Concat(monoMethod.GetParameters().Select(it => it.ParameterType.NativeType()))
                .ToArray());

        if (monoMethod.ReturnType == typeof(void))
        {
            body.Emit(OpCodes.Ldc_I4_0);
            body.Emit(OpCodes.Conv_I);
        }
        else if (monoMethod.ReturnType.IsValueType)
        {
            var returnValue = body.DeclareLocal(monoMethod.ReturnType);
            body.Emit(OpCodes.Stloc, returnValue);
            var classField = typeof(Il2CppClassPointerStore<>).MakeGenericType(monoMethod.ReturnType)
                .GetField(nameof(Il2CppClassPointerStore<int>.NativeClassPtr));
            body.Emit(OpCodes.Ldsfld, classField);
            body.Emit(OpCodes.Ldloca, returnValue);
            body.Emit(OpCodes.Call, typeof(IL2CPP).GetMethod(nameof(IL2CPP.il2cpp_value_box))!);
        }

        body.Emit(OpCodes.Ret);

        GCHandle.Alloc(method);

        var @delegate = (InvokerDelegate)method.CreateDelegate(typeof(InvokerDelegate));
        GCHandle.Alloc(@delegate);
        return @delegate;
    }

    private static IntPtr StaticVoidIntPtrInvoker(IntPtr methodPointer, Il2CppMethodInfo* methodInfo, IntPtr obj,
        IntPtr* args)
    {
        Marshal.GetDelegateForFunctionPointer<VoidCtorDelegate>(methodPointer)(obj);
        return IntPtr.Zero;
    }

    private static Delegate CreateTrampoline(MethodInfo monoMethod)
    {
        var nativeParameterTypes = new[] { typeof(IntPtr) }.Concat(monoMethod.GetParameters()
            .Select(it => it.ParameterType.NativeType()).Concat(new[] { typeof(Il2CppMethodInfo*) })).ToArray();

        var managedParameters = new[] { monoMethod.DeclaringType }
            .Concat(monoMethod.GetParameters().Select(it => it.ParameterType)).ToArray();

        var method = new DynamicMethod(
            "Trampoline_" + ExtractSignature(monoMethod) + monoMethod.DeclaringType + monoMethod.Name,
            MethodAttributes.Static | MethodAttributes.Public, CallingConventions.Standard,
            monoMethod.ReturnType.NativeType(), nativeParameterTypes,
            monoMethod.DeclaringType, true);

        var signature = new DelegateSupport.MethodSignature(monoMethod, true);
        var delegateType = DelegateSupport.GetOrCreateDelegateType(signature, monoMethod);

        var body = method.GetILGenerator();

        body.BeginExceptionBlock();

        body.Emit(OpCodes.Ldarg_0);
        body.Emit(OpCodes.Call,
            typeof(ClassInjectorBase).GetMethod(nameof(ClassInjectorBase.GetMonoObjectFromIl2CppPointer))!);
        body.Emit(OpCodes.Castclass, monoMethod.DeclaringType);

        var indirectVariables = new LocalBuilder[managedParameters.Length];

        for (var i = 1; i < managedParameters.Length; i++)
        {
            var parameter = managedParameters[i];
            if (parameter.IsSubclassOf(typeof(ValueType)))
            {
                body.Emit(OpCodes.Ldc_I8, Il2CppClassPointerStore.GetNativeClassPointer(parameter).ToInt64());
                body.Emit(OpCodes.Conv_I);
                body.Emit(Environment.Is64BitProcess ? OpCodes.Ldarg : OpCodes.Ldarga_S, i);
                body.Emit(OpCodes.Call, typeof(IL2CPP).GetMethod(nameof(IL2CPP.il2cpp_value_box)));
            }
            else
            {
                body.Emit(OpCodes.Ldarg, i);
            }

            if (parameter.IsValueType) continue;

            void HandleTypeConversion(Type type)
            {
                if (type == typeof(string))
                {
                    body.Emit(OpCodes.Call, typeof(IL2CPP).GetMethod(nameof(IL2CPP.Il2CppStringToManaged))!);
                }
                else if (type.IsSubclassOf(typeof(Il2CppObjectBase)))
                {
                    var labelNull = body.DefineLabel();
                    var labelNotNull = body.DefineLabel();
                    body.Emit(OpCodes.Dup);
                    body.Emit(OpCodes.Brfalse, labelNull);
                    // We need to directly resolve from all constructors because on mono GetConstructor can cause the following issue:
                    // `Missing field layout info for ...`
                    // This is caused by GetConstructor calling RuntimeTypeHandle.CanCastTo which can fail since right now unhollower emits ALL fields which appear to now work properly
                    body.Emit(OpCodes.Newobj, type.GetConstructors().FirstOrDefault(ci =>
                    {
                        var ps = ci.GetParameters();
                        return ps.Length == 1 && ps[0].ParameterType == typeof(IntPtr);
                    })!);
                    body.Emit(OpCodes.Br, labelNotNull);
                    body.MarkLabel(labelNull);
                    body.Emit(OpCodes.Pop);
                    body.Emit(OpCodes.Ldnull);
                    body.MarkLabel(labelNotNull);
                }
            }

            if (parameter.IsByRef)
            {
                var elemType = parameter.GetElementType();

                indirectVariables[i] = body.DeclareLocal(elemType);

                body.Emit(OpCodes.Ldind_I);
                HandleTypeConversion(elemType);
                body.Emit(OpCodes.Stloc, indirectVariables[i]);
                body.Emit(OpCodes.Ldloca, indirectVariables[i]);
            }
            else
            {
                HandleTypeConversion(parameter);
            }
        }

        body.Emit(OpCodes.Call, monoMethod);
        LocalBuilder managedReturnVariable = null;
        if (monoMethod.ReturnType != typeof(void))
        {
            managedReturnVariable = body.DeclareLocal(monoMethod.ReturnType);
            body.Emit(OpCodes.Stloc, managedReturnVariable);
        }

        for (var i = 1; i < managedParameters.Length; i++)
        {
            var variable = indirectVariables[i];
            if (variable == null)
                continue;
            body.Emit(OpCodes.Ldarg_S, i);
            body.Emit(OpCodes.Ldloc, variable);
            var directType = managedParameters[i].GetElementType();
            if (directType == typeof(string))
                body.Emit(OpCodes.Call, typeof(IL2CPP).GetMethod(nameof(IL2CPP.ManagedStringToIl2Cpp))!);
            else if (!directType.IsValueType)
                body.Emit(OpCodes.Call, typeof(IL2CPP).GetMethod(nameof(IL2CPP.Il2CppObjectBaseToPtr))!);
            body.Emit(InjectorHelpers.StIndOpcodes.TryGetValue(directType, out var stindOpCodde)
                ? stindOpCodde
                : OpCodes.Stind_I);
        }
        // body.Emit(OpCodes.Ret); // breaks coreclr

        var exceptionLocal = body.DeclareLocal(typeof(Exception));
        body.BeginCatchBlock(typeof(Exception));
        body.Emit(OpCodes.Stloc, exceptionLocal);
        body.Emit(OpCodes.Ldstr, "Exception in IL2CPP-to-Managed trampoline, not passing it to il2cpp: ");
        body.Emit(OpCodes.Ldloc, exceptionLocal);
        body.Emit(OpCodes.Callvirt, typeof(object).GetMethod(nameof(ToString))!);
        body.Emit(OpCodes.Call,
            typeof(string).GetMethod(nameof(string.Concat), new[] { typeof(string), typeof(string) })!);
        body.Emit(OpCodes.Call, typeof(ClassInjector).GetMethod(nameof(LogError), BindingFlags.Static | BindingFlags.NonPublic)!);

        body.EndExceptionBlock();

        if (managedReturnVariable != null)
        {
            body.Emit(OpCodes.Ldloc, managedReturnVariable);
            if (monoMethod.ReturnType == typeof(string))
                body.Emit(OpCodes.Call, typeof(IL2CPP).GetMethod(nameof(IL2CPP.ManagedStringToIl2Cpp))!);
            else if (!monoMethod.ReturnType.IsValueType)
                body.Emit(OpCodes.Call, typeof(IL2CPP).GetMethod(nameof(IL2CPP.Il2CppObjectBaseToPtr))!);
        }

        body.Emit(OpCodes.Ret);

        var @delegate = method.CreateDelegate(delegateType);
        GCHandle.Alloc(@delegate); // pin it forever
        return @delegate;
    }

    private static void LogError(string message)
    {
        Logger.Instance.LogError("{Message}", message);
    }

    private static string ExtractSignature(MethodInfo monoMethod)
    {
        var builder = new StringBuilder();
        builder.Append(monoMethod.ReturnType.NativeType().Name);
        builder.Append(monoMethod.IsStatic ? "" : "This");
        foreach (var parameterInfo in monoMethod.GetParameters())
            builder.Append(parameterInfo.ParameterType.NativeType().Name);
        return builder.ToString();
    }

    private static Type RewriteType(Type type)
    {
        if (type.IsValueType && !type.IsEnum)
            return type;

        if (type.IsArray)
        {
            var elementType = type.GetElementType();
            if (elementType!.FullName == "System.String") return typeof(Il2CppStringArray);

            var convertedElementType = RewriteType(elementType);
            if (elementType.IsGenericParameter) return typeof(Il2CppArrayBase<>).MakeGenericType(convertedElementType);

            return (convertedElementType.IsValueType ? typeof(Il2CppStructArray<>) : typeof(Il2CppReferenceArray<>))
                .MakeGenericType(convertedElementType);
        }

        if (type.FullName!.StartsWith("System"))
        {
            var fullName = $"Il2Cpp{type.FullName}";
            var resolvedType = Type.GetType($"{fullName}, Il2Cpp{type.Assembly.GetName().Name}", false);
            if (resolvedType != null)
                return resolvedType;

            return AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType(fullName, false))
                .First(t => t != null);
        }

        return type;
    }

    private static string GetIl2CppTypeFullName(Il2CppTypeStruct* typePointer)
    {
        var klass = UnityVersionHandler.Wrap((Il2CppClass*)IL2CPP.il2cpp_class_from_type((IntPtr)typePointer));
        var assembly = UnityVersionHandler.Wrap(UnityVersionHandler.Wrap(klass.Image).Assembly);

        var fullName = new StringBuilder();

        var namespaceName = Marshal.PtrToStringAnsi(klass.Namespace);
        if (!string.IsNullOrEmpty(namespaceName))
        {
            fullName.Append(namespaceName);
            fullName.Append('.');
        }

        var declaringType = klass;
        while ((declaringType = UnityVersionHandler.Wrap(declaringType.DeclaringType)) != default)
        {
            fullName.Append(Marshal.PtrToStringAnsi(declaringType.Name));
            fullName.Append('+');
        }

        fullName.Append(Marshal.PtrToStringAnsi(klass.Name));

        var assemblyName = Marshal.PtrToStringAnsi(assembly.Name.Name);
        if (assemblyName != "mscorlib")
        {
            fullName.Append(", ");
            fullName.Append(assemblyName);
        }

        return fullName.ToString();
    }

    private static Type SystemTypeFromIl2CppType(Il2CppTypeStruct* typePointer)
    {
        var fullName = GetIl2CppTypeFullName(typePointer);
        var type = Type.GetType(fullName) ?? throw new NullReferenceException($"Couldn't find System.Type for Il2Cpp type: {fullName}");
        return RewriteType(type);
    }

    internal static Il2CppMethodInfo* hkGenericMethodGetMethod(Il2CppGenericMethod* gmethod, bool copyMethodPtr)
    {
        if (InflatedMethodFromContextDictionary.TryGetValue((IntPtr)gmethod->methodDefinition, out var methods))
        {
            var instancePointer = gmethod->context.method_inst;
            if (methods.Item2.TryGetValue((IntPtr)instancePointer, out var inflatedMethodPointer))
                return (Il2CppMethodInfo*)inflatedMethodPointer;

            var typeArguments = new Type[instancePointer->type_argc];
            for (var i = 0; i < instancePointer->type_argc; i++)
                typeArguments[i] = SystemTypeFromIl2CppType(instancePointer->type_argv[i]);
            var inflatedMethod = methods.Item1.MakeGenericMethod(typeArguments);
            Logger.Instance.LogTrace("Inflated method: {InflatedMethod}", inflatedMethod.Name);
            inflatedMethodPointer = (IntPtr)ConvertMethodInfo(inflatedMethod,
                UnityVersionHandler.Wrap(UnityVersionHandler.Wrap(gmethod->methodDefinition).Class));
            methods.Item2.Add((IntPtr)instancePointer, inflatedMethodPointer);

            return (Il2CppMethodInfo*)inflatedMethodPointer;
        }

        return InjectorHelpers.GenericMethodGetMethodOriginal(gmethod, copyMethodPtr);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr InvokerDelegate(IntPtr methodPointer, Il2CppMethodInfo* methodInfo, IntPtr obj,
        IntPtr* args);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void VoidCtorDelegate(IntPtr objectPointer);
}
