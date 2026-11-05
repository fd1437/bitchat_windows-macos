using System.IO.Compression;
using System.Runtime.InteropServices;
namespace BitChat.Core;

/// Uses pinned reference zlib on Windows: the runtime's CPU-specific compressor
/// can choose different matches, while upstream signs the re-encoded wire bytes.
public static class CanonicalDeflate
{
    static CanonicalDeflate()
    {
        if(OperatingSystem.IsWindows())NativeLibrary.SetDllImportResolver(typeof(CanonicalDeflate).Assembly,(name,assembly,path)=>
        {if(name!="bitchat_zlib")return IntPtr.Zero;string rid=RuntimeInformation.ProcessArchitecture==Architecture.Arm64?"win-arm64":"win-x64";return NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory,"native",rid,"bitchat_zlib.dll"));});
    }
    [DllImport("bitchat_zlib",EntryPoint="compress2",CallingConvention=CallingConvention.Cdecl)]
    private static extern int Compress(byte[] destination,ref uint size,byte[] input,uint inputSize,int level);
    public static byte[] Encode(byte[] bytes)
    {
        if(!OperatingSystem.IsWindows()){using var output=new MemoryStream();using(var z=new DeflateStream(output,CompressionLevel.Optimal,true))z.Write(bytes);return output.ToArray();}
        var wrapped=new byte[checked(bytes.Length+bytes.Length/1000+64)];uint n=(uint)wrapped.Length;int result=Compress(wrapped,ref n,bytes,(uint)bytes.Length,6);
        if(result!=0||n<6)throw new InvalidDataException("Reference zlib compression failed: "+result);
        return wrapped.AsSpan(2,checked((int)n-6)).ToArray();
    }
}
