using System;
using System.Runtime.InteropServices;
namespace Il2CppInterop.Runtime.Runtime.VersionSpecific.Image
{
    [ApplicableToUnityVersionsSince("2018.1.0")]
    public unsafe class NativeImageStructHandler_24_2 : INativeImageStructHandler
    {
        public int Size() => sizeof(Il2CppImage_24_2);
        public INativeImageStruct CreateNewStruct()
        {
            IntPtr ptr = Marshal.AllocHGlobal(Size());
            Il2CppImage_24_2* _ = (Il2CppImage_24_2*)ptr;
            *_ = default;
            return new NativeStructWrapper(ptr);
        }
        public INativeImageStruct Wrap(Il2CppImage* ptr)
        {
            if (ptr == null) return null;
            return new NativeStructWrapper((IntPtr)ptr);
        }
        internal unsafe struct Il2CppImage_24_2
        {
            public byte* name;
            public byte* nameNoExt;
            public Il2CppAssembly* assembly;
            public int typeStart;
            public uint typeCount;
            public int exportedTypeStart;
            public uint exportedTypeCount;
            public int entryPointIndex;
            public void* nameToClassHashTable;
            public uint token;
            public byte dynamic;
        }

        internal class NativeStructWrapper : INativeImageStruct
        {
            public NativeStructWrapper(IntPtr ptr) => Pointer = ptr;
            public IntPtr Pointer { get; }
            private Il2CppImage_24_2* _ => (Il2CppImage_24_2*)Pointer;
            public Il2CppImage* ImagePointer => (Il2CppImage*)Pointer;
            public bool HasNameNoExt => true;
            public ref Il2CppAssembly* Assembly => ref _->assembly;
            public ref byte Dynamic => ref _->dynamic;
            public ref IntPtr Name => ref *(IntPtr*)&_->name;
            public ref IntPtr NameNoExt => ref *(IntPtr*)&_->nameNoExt;
        }

    }

}
