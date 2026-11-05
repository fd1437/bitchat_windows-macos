using System.Diagnostics;
using System.IO;
using System.Threading.Channels;
using NAudio.Wave;
using BitChat.Core;

namespace BitChat.Windows;

public static class AudioProcess
{
    public static async Task<MediaPacket> VoiceNote(byte[] adts,string? burst=null)
    {
        string temporary=Path.Combine(Path.GetTempPath(),"bitchat-audio-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temporary);
        string output=Path.Combine(temporary,"note.m4a");
        try
        {
            using var process=Start(["-f","aac","-i","pipe:0","-c:a","copy","-movflags","+faststart","-f","ipod",output]);
            var errors=process.StandardError.ReadToEndAsync();
            await process.StandardInput.BaseStream.WriteAsync(adts);process.StandardInput.Close();
            try{await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));if(process.ExitCode!=0)throw new IOException(await errors);}
            finally{if(!process.HasExited)process.Kill(true);}
            var bytes=await File.ReadAllBytesAsync(output);
            if(bytes.Length<12||!bytes.AsSpan(4,4).SequenceEqual("ftyp"u8))throw new InvalidDataException("AAC remux did not produce an M4A container");
            return new("voice_"+(burst??Guid.NewGuid().ToString().ToUpperInvariant())+".m4a","audio/mp4",bytes);
        }
        finally{Directory.Delete(temporary,true);}
    }
    public static Process Start(string[] args)
    {
        string path=Path.Combine(AppContext.BaseDirectory,"media","ffmpeg.exe");if(!File.Exists(path))throw new FileNotFoundException("Bundled AAC codec is missing",path);
        var info=new ProcessStartInfo(path){CreateNoWindow=true,UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in new[]{"-hide_banner","-loglevel","error"}.Concat(args))info.ArgumentList.Add(arg);
        var process=Process.Start(info)??throw new IOException("Could not launch AAC codec");return process;
    }
}
public sealed class VoiceRecorder:IAsyncDisposable
{
    private readonly WaveInEvent capture=new(){WaveFormat=new WaveFormat(16000,16,1),BufferMilliseconds=64};
    private readonly Channel<byte[]> pcm=Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64){FullMode=BoundedChannelFullMode.Wait,SingleReader=true});
    private readonly MemoryStream recording=new();
    private Process? process;private Task? input,output;private Task<string>? errors;private long started;private bool stopped;
    public event Action<byte[]>? Frame;
    public event Action<string>? Error;
    public void Start()
    {
        if(process!=null)throw new InvalidOperationException("Already recording");
        process=AudioProcess.Start(["-f","s16le","-ar","16000","-ac","1","-i","pipe:0","-c:a","aac","-profile:a","aac_low","-b:a","16000","-f","adts","-flush_packets","1","pipe:1"]);
        errors=process.StandardError.ReadToEndAsync();started=Bytes.Now;
        capture.DataAvailable+=(_,e)=>{if(Bytes.Now-started>120000||!pcm.Writer.TryWrite(e.Buffer.AsSpan(0,e.BytesRecorded).ToArray())){capture.StopRecording();Error?.Invoke("Recording stopped at its duration/buffer limit");}};
        capture.RecordingStopped+=(_,e)=>{pcm.Writer.TryComplete(e.Exception);if(e.Exception!=null)Error?.Invoke(e.Exception.Message);};
        input=Task.Run(async()=>{try{await foreach(var bytes in pcm.Reader.ReadAllAsync())await process.StandardInput.BaseStream.WriteAsync(bytes);}finally{process.StandardInput.Close();}});
        output=Task.Run(async()=>
        {
            try
            {
                var stream=process.StandardOutput.BaseStream;var header=new byte[7];
                while(true)
                {
                    int n=await stream.ReadAsync(header.AsMemory(0,1));if(n==0)break;await stream.ReadExactlyAsync(header.AsMemory(1,6));
                    if(header[0]!=0xff||(header[1]&0xf6)!=0xf0)throw new InvalidDataException("Invalid AAC codec output");
                    int length=((header[3]&3)<<11)|(header[4]<<3)|(header[5]>>5);bool crc=(header[1]&1)==0;if(length<7+(crc?2:0)||length>8191)throw new InvalidDataException("Invalid AAC frame length");
                    if(crc)await stream.ReadExactlyAsync(new byte[2]);var raw=new byte[length-7-(crc?2:0)];await stream.ReadExactlyAsync(raw);
                    if(recording.Length+raw.Length+7>512*1024)throw new InvalidDataException("Voice note exceeds 512 KiB");recording.Write(VoicePacket.Adts(raw));Frame?.Invoke(raw);
                }
            }
            catch(Exception e){Error?.Invoke(e.Message);throw;}
        });capture.StartRecording();
    }
    public async Task<MediaPacket> Stop()
    {
        if(process==null)throw new InvalidOperationException("Not recording");if(!stopped){stopped=true;capture.StopRecording();pcm.Writer.TryComplete();}
        await Task.WhenAll(input!,output!).WaitAsync(TimeSpan.FromSeconds(15));await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        if(process.ExitCode!=0)throw new IOException(await errors!);if(recording.Length==0)throw new IOException("No audio captured");
        return await AudioProcess.VoiceNote(recording.ToArray());
    }
    public async ValueTask DisposeAsync()
    {
        capture.Dispose();pcm.Writer.TryComplete();if(process is {HasExited:false})process.Kill(true);if(input!=null)try{await Task.WhenAll(input,output??Task.CompletedTask);}catch{}process?.Dispose();recording.Dispose();
    }
}

public sealed class VoicePlayback:IAsyncDisposable
{
    private readonly Process process;
    private readonly WaveOutEvent player=new();
    private readonly BufferedWaveProvider pcm=new(new WaveFormat(16000,16,1)){BufferDuration=TimeSpan.FromSeconds(10),DiscardOnBufferOverflow=true};
    private readonly Channel<byte[]> frames=Channel.CreateBounded<byte[]>(128);
    private readonly Task input,output;private readonly Task<string> errors;private int disposed;
    public VoicePlayback()
    {
        process=AudioProcess.Start(["-probesize","32","-analyzeduration","0","-f","aac","-i","pipe:0","-f","s16le","-ar","16000","-ac","1","-flush_packets","1","pipe:1"]);
        errors=process.StandardError.ReadToEndAsync();player.Init(pcm);player.Play();
        input=Task.Run(async()=>{try{await foreach(var bytes in frames.Reader.ReadAllAsync())await process.StandardInput.BaseStream.WriteAsync(bytes);}finally{process.StandardInput.Close();}});
        output=Task.Run(async()=>{var bytes=new byte[4096];int n;while((n=await process.StandardOutput.BaseStream.ReadAsync(bytes))>0)pcm.AddSamples(bytes,0,n);});
    }
    public void Add(byte[] raw){if(disposed!=0)return;if(!frames.Writer.TryWrite(VoicePacket.Adts(raw)))throw new IOException("Live voice playback buffer is full");}
    public async Task Complete(){if(disposed!=0)return;frames.Writer.TryComplete();await Task.WhenAll(input,output).WaitAsync(TimeSpan.FromSeconds(10));await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));if(process.ExitCode!=0)throw new IOException(await errors);await Task.Delay(Math.Min(5000,(int)pcm.BufferedDuration.TotalMilliseconds+100));}
    public async ValueTask DisposeAsync(){if(Interlocked.Exchange(ref disposed,1)!=0)return;frames.Writer.TryComplete();if(!process.HasExited)process.Kill(true);try{await Task.WhenAll(input,output);}catch{}player.Dispose();process.Dispose();}
}
