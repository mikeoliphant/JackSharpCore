using Jack.NAudio;
using JackSharp;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

// Records the first two physical inputs to test.wav, then plays example.wav to the first two physical outputs, through
// one JACK client.

Processor processor;
try
{
    processor = new Processor("AudioTranscription");
}
catch (JackException ex)
{
    Console.WriteLine(ex.NoServer ? "Start a JACK server first." : ex.Message);
    return 1;
}
using var client = processor;
AudioPort[] inputs = { processor.RegisterAudioPort("in_1", Direction.In), processor.RegisterAudioPort("in_2", Direction.In) };
AudioPort[] outputs = { processor.RegisterAudioPort("out_1", Direction.Out), processor.RegisterAudioPort("out_2", Direction.Out) };
using var audioIn = new AudioIn(processor);
using var audioOut = new AudioOut(processor);
processor.Activate();

// Connect to the sound card: its capture ports send, its playback ports receive.
var captures = processor.GetPorts(type: PortType.Audio, direction: Direction.Out, physicalOnly: true);
var playbacks = processor.GetPorts(type: PortType.Audio, direction: Direction.In, physicalOnly: true);
for (int i = 0; i < 2; i++)
{
    if (i < captures.Count)
        inputs[i].ConnectTo(captures[i].FullName);
    if (i < playbacks.Count)
        outputs[i].ConnectTo(playbacks[i].FullName);
}

Console.WriteLine($"Recording at {processor.SampleRate} Hz");
var waveOutFile = new WaveFileWriter("test.wav", audioIn.WaveFormat);
// DataAvailable runs on JACK's real-time thread: a file write is fine for a sample, not for a real program.
audioIn.DataAvailable += (_, e) => waveOutFile.Write(e.Buffer, 0, e.BytesRecorded);
audioIn.RecordingStopped += (_, _) =>
{
    Console.WriteLine("Recording stopped");
    waveOutFile.Dispose();
};
audioIn.StartRecording();
Console.WriteLine("Press any key to stop recording");
Console.ReadKey();
audioIn.StopRecording();

Console.WriteLine("Press any key to start playback");
Console.ReadKey();

var wavInFile = new AudioFileReader("example.wav");
Console.WriteLine($"Sample rate: {wavInFile.WaveFormat.SampleRate}, channels: {wavInFile.WaveFormat.Channels}");
// JACK plays float samples at the server's rate, one channel per output.
ISampleProvider playable = wavInFile;
if (playable.WaveFormat.Channels == 1)
    playable = new MonoToStereoSampleProvider(playable);
if (playable.WaveFormat.SampleRate != processor.SampleRate)
    playable = new WdlResamplingSampleProvider(playable, processor.SampleRate);
audioOut.Init(playable.ToWaveProvider());
audioOut.Play();
Console.WriteLine("Press any key to stop playback");
Console.ReadKey();
audioOut.Stop();
return 0;
