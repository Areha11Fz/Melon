using System;
using System.Runtime.InteropServices;

namespace UnhollowerBaseLib
{
    public class Il2CppObjectBase
    {
        public IntPtr Pointer => IL2CPP.il2cpp_gchandle_get_target(myGcHandle);
    
        private readonly uint myGcHandle;

        public Il2CppObjectBase(IntPtr pointer)
        {
            if (pointer == IntPtr.Zero)
                throw new NullReferenceException();
        
            myGcHandle = IL2CPP.il2cpp_gchandle_new(pointer, false);
        }

        public T Cast<T>() where T: Il2CppObjectBase
        {
            return TryCast<T>() ?? throw new InvalidCastException($"Can't cast object of type {Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(IL2CPP.il2cpp_object_get_class(Pointer)))} to type {typeof(T)}");
        }
        
        public T TryCast<T>() where T: Il2CppObjectBase
        {
            var nestedTypeClassPointer = Il2CppClassPointerStore<T>.NativeClassPtr;
            if (nestedTypeClassPointer == IntPtr.Zero)
                throw new ArgumentException($"{typeof(T)} is not al Il2Cpp reference type");

            // todo: support arrays
            
            var ownClass = IL2CPP.il2cpp_object_get_class(Pointer);
            if (!IL2CPP.il2cpp_class_is_assignable_from(nestedTypeClassPointer, ownClass))
                return null;

            return (T) Activator.CreateInstance(typeof(T), Pointer);
        }

        ~Il2CppObjectBase()
        {
            IL2CPP.il2cpp_gchandle_free(myGcHandle);
        }
    }
}