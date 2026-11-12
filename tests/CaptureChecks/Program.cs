using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vibershot.Core;
using Vibershot.Media;
class Program {
 static void Check(bool ok, string what) { if (!ok) throw new Exception("FAIL: " + what); }
 static void Same(string[] expected, string[] actual, string what) { if (!expected.SequenceEqual(actual)) throw new Exception($"FAIL: {what}\n expected: {string.Join(" ", expected)}\n actual:   {string.Join(" ", actual)}"); }
 [STAThread] static void Main() {
  BitmapSource Frame(int offset) { int w=120,h=100; byte[] p=new byte[w*h*4]; for(int y=0;y<h;y++)for(int x=0;x<w;x++){int i=(y*w+x)*4; p[i]=(byte)((x*17+(y+offset)*7)%256);p[i+1]=(byte)((x*3+(y+offset)*13)%256);p[i+2]=(byte)((x*11+(y+offset)*19)%256);p[i+3]=255;} var b=BitmapSource.Create(w,h,96,96,PixelFormats.Bgra32,null,p,w*4);b.Freeze();return b; }
  var first=Frame(0);var next=Frame(40);var stitched=ScrollStitcher.Append(first,first,next);if(stitched.PixelHeight!=140)throw new Exception("Wrong overlap");
  var expected=Frame(0); byte[] pixels=new byte[120*140*4];stitched.CopyPixels(pixels,480,0);byte[] tail=new byte[480*100];next.CopyPixels(tail,480,0);for(int i=0;i<480*40;i++)if(pixels[480*100+i]!=tail[480*60+i])throw new Exception("Wrong stitched pixels");
  bool rejected=false;try{ScrollStitcher.Append(first,first,first);}catch(InvalidOperationException){rejected=true;}if(!rejected)throw new Exception("Duplicate accepted");
  var path=Path.Combine(AppContext.BaseDirectory,"test.avi");File.Delete(path);using(var writer=new VideoWriter(path,120,100)){writer.AddFrame(first);using var recovery=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);byte[] checkpoint=new byte[checked((int)recovery.Length)];recovery.ReadExactly(checkpoint);if(BitConverter.ToInt32(checkpoint,4)!=checkpoint.Length-8||BitConverter.ToInt32(checkpoint,48)!=1)throw new Exception("Invalid recovery checkpoint");writer.AddFrame(next);}
  byte[] avi=File.ReadAllBytes(path);if(Encoding.ASCII.GetString(avi,0,4)!="RIFF"||BitConverter.ToInt32(avi,4)!=avi.Length-8||BitConverter.ToInt32(avi,48)!=2)throw new Exception("Invalid AVI header");
  int pos=12;while(pos<avi.Length){string id=Encoding.ASCII.GetString(avi,pos,4);int size=BitConverter.ToInt32(avi,pos+4);if(size<0||pos+8+size>avi.Length)throw new Exception("Invalid AVI chunk");pos+=8+size+(size&1);}if(pos!=avi.Length)throw new Exception("Bad AVI size");
  var config=JsonSerializer.Deserialize<AppConfig>("{\"Autosave\":true}");if(!config.ShowCaptureActions||config.ShowSaveDialog)throw new Exception("Legacy config defaults");
  if(config.PrintScreen!=PrintScreenAction.Off)throw new Exception("Print Screen should be off by default");
  var ps=JsonSerializer.Deserialize<AppConfig>("{\"PrintScreen\":\"FullDesktop\"}");if(ps.PrintScreen!=PrintScreenAction.FullDesktop)throw new Exception("Print Screen setting not read");
  if(config.WebpQuality!=80)throw new Exception("WebP quality default");
  var legacyWebp=JsonSerializer.Deserialize<AppConfig>("{\"Format\":2}");if(legacyWebp.Format!=ImageFormat.Webp)throw new Exception("Legacy WebP enum value changed");
  foreach(var (fmt,lossless) in new[]{(ImageFormat.Webp,false),(ImageFormat.WebpLossless,true)}){
   var ms=new MemoryStream();ImageEncoding.Encode(first,fmt,60,ms);byte[] webp=ms.ToArray();
   if(webp.Length<12||Encoding.ASCII.GetString(webp,0,4)!="RIFF"||Encoding.ASCII.GetString(webp,8,4)!="WEBP")throw new Exception("Invalid WebP header for "+fmt);
   string chunk=Encoding.ASCII.GetString(webp,12,4);if(lossless?chunk!="VP8L":chunk!="VP8 ")throw new Exception("Unexpected WebP chunk "+chunk+" for "+fmt);
   using var decoded=SkiaSharp.SKBitmap.Decode(webp);if(decoded is null||decoded.Width!=120||decoded.Height!=100)throw new Exception("WebP size mismatch for "+fmt);
   if(lossless){byte[] orig=new byte[120*100*4];first.CopyPixels(orig,480,0);for(int y=0;y<100;y++)for(int x=0;x<120;x++){var c=decoded.GetPixel(x,y);int i=(y*120+x)*4;if(c.Blue!=orig[i]||c.Green!=orig[i+1]||c.Red!=orig[i+2]||c.Alpha!=orig[i+3])throw new Exception("Lossless WebP changed pixels");}}
   if(ImageEncoding.Extension(fmt)!="webp")throw new Exception("WebP extension");
  }
  Console.WriteLine("PASS: overlap and pixels, duplicate rejection, AVI frame counts/chunk sizes, legacy settings defaults, WebP lossy/lossless encoding.");

  // Video settings: legacy configs without the section load with MP4 defaults; a null section is replaced.
  Check(config.Video is not null && config.Video.Format==VideoFormat.Mp4 && config.Video.FrameRate=="30" && config.Video.Quality==75 && config.Video.PreferHardwareEncoder && config.Video.ShowCursor, "legacy config gets default Video settings");
  Check(JsonSerializer.Deserialize<AppConfig>("{\"Video\":null}")!.Video is not null, "null Video section replaced by defaults");
  var videoCfg=JsonSerializer.Deserialize<AppConfig>("{\"Video\":{\"Format\":\"Avi\",\"FrameRate\":\"59.94\",\"Quality\":40,\"PreferHardwareEncoder\":false,\"ShowCursor\":false}}")!;
  Check(videoCfg.Video.Format==VideoFormat.Avi && videoCfg.Video.FrameRate=="59.94" && videoCfg.Video.Quality==40 && !videoCfg.Video.PreferHardwareEncoder && !videoCfg.Video.ShowCursor, "Video section round trip");
  string serialized=JsonSerializer.Serialize(new AppConfig()); Check(serialized.Contains("\"Video\"") && serialized.Contains("\"Format\":\"Mp4\""), "Video section serializes with enum names");

  // FrameRate rational math, parsing and display.
  Check(FrameRate.Parse("29.97")==FrameRate.Ntsc30 && FrameRate.Parse("30000/1001")==FrameRate.Ntsc30 && FrameRate.Parse("29.970")==FrameRate.Ntsc30, "29.97 parses to 30000/1001");
  Check(FrameRate.Ntsc30.ToString()=="29.97" && FrameRate.Ntsc30.ToFfmpegString()=="30000/1001", "29.97 formats as 29.97 / 30000/1001");
  Check(FrameRate.Parse("59.94")==FrameRate.Ntsc60 && FrameRate.Ntsc60.ToString()=="59.94" && FrameRate.Parse("23.976").ToString()=="23.98", "NTSC 60 and 23.976");
  Check(FrameRate.Parse("60")==FrameRate.Fps60 && FrameRate.Fps60.ToString()=="60" && FrameRate.Fps60.ToFfmpegString()=="60", "integer rate");
  Check(new FrameRate(60,2)==FrameRate.Fps30 && new FrameRate(60000,2002)==FrameRate.Ntsc30, "rationals reduce");
  Check(FrameRate.Parse("15.5").Numerator==31 && FrameRate.Parse("15.5").Denominator==2, "custom decimal becomes exact rational");
  Check(FrameRate.Ntsc30.Rounded==30 && FrameRate.Ntsc60.Rounded==60 && FrameRate.Fps24.Rounded==24, "rounded fps");
  Check(FrameRate.Ntsc30.FramesIn(TimeSpan.FromMinutes(1))==1798 && FrameRate.Fps60.FramesIn(TimeSpan.FromSeconds(2))==120, "frames in a duration");
  Check(Math.Abs(FrameRate.Ntsc60.FrameDuration.TotalMilliseconds-16.683)<0.001, "frame duration");
  Check(!FrameRate.TryParse("0",out _) && !FrameRate.TryParse("abc",out _) && !FrameRate.TryParse("30/0",out _) && !FrameRate.TryParse("",out _), "bad frame rates rejected");
  Check(FrameRate.Presets.Count==7 && FrameRate.Presets[2]==FrameRate.Ntsc30, "presets");

  // Quality mapping and even-dimension rounding.
  Check(RecordingArguments.QualityToCrf(100)==19 && RecordingArguments.QualityToCrf(75)==23 && RecordingArguments.QualityToCrf(1)==35 && RecordingArguments.QualityToCrf(500)==19, "quality to CRF");
  Check(new PixelRect(3,5,1281,721).ToEven()==new PixelRect(3,5,1280,720) && new PixelRect(0,0,1,1).ToEven()==new PixelRect(0,0,2,2), "even rounding");
  Check(new PixelRect(0,0,100,100).Intersect(new PixelRect(50,50,100,100))==new PixelRect(50,50,50,50) && new PixelRect(0,0,10,10).Intersect(new PixelRect(20,20,5,5)).IsEmpty, "rect intersection");
  var display=new DisplayInfo(1,new PixelRect(2560,0,2560,1440),false,"\\\\.\\DISPLAY2");
  Check(Displays.Containing(new PixelRect(2600,100,100,100),new[]{new DisplayInfo(0,new PixelRect(0,0,2560,1440),true,"\\\\.\\DISPLAY1"),display})==display, "display containing a region");

  // Exact ffmpeg arguments for a 60 fps ddagrab recording on the second monitor with NVENC.
  var nvenc=new RecordingOptions{Region=new PixelRect(2660,100,1281,721),Display=display,FrameRate=FrameRate.Fps60,Quality=75,ShowCursor=true,Encoder=EncoderProbe.Hardware[0],OutputPath=@"C:\rec\recording_part01.mp4"};
  Same(new[]{"-hide_banner","-nostats","-progress","pipe:1","-f","lavfi","-i","ddagrab=output_idx=1:framerate=60:draw_mouse=1:offset_x=100:offset_y=100:video_size=1280x720,hwdownload,format=bgra","-c:v","h264_nvenc","-preset","p4","-tune","hq","-rc","vbr","-cq","23","-b:v","0","-g","60","-r","60","-pix_fmt","yuv420p","-movflags","frag_keyframe+empty_moov","-f","mp4","-y",@"C:\rec\recording_part01.mp4"}, RecordingArguments.Ddagrab(nvenc), "ddagrab 60 fps nvenc arguments");
  var ntsc=nvenc with {FrameRate=FrameRate.Ntsc30,ShowCursor=false,Encoder=new EncoderInfo("libx264","x264",false),Quality=100};
  Same(new[]{"-hide_banner","-nostats","-progress","pipe:1","-f","lavfi","-i","ddagrab=output_idx=1:framerate=30000/1001:draw_mouse=0:offset_x=100:offset_y=100:video_size=1280x720,hwdownload,format=bgra","-c:v","libx264","-preset","veryfast","-crf","19","-g","30","-r","30000/1001","-pix_fmt","yuv420p","-movflags","frag_keyframe+empty_moov","-f","mp4","-y",@"C:\rec\recording_part01.mp4"}, RecordingArguments.Ddagrab(ntsc), "ddagrab 29.97 x264 arguments");
  Same(new[]{"-hide_banner","-nostats","-progress","pipe:1","-f","gdigrab","-framerate","60","-draw_mouse","1","-offset_x","2660","-offset_y","100","-video_size","1280x720","-i","desktop","-c:v","h264_nvenc","-preset","p4","-tune","hq","-rc","vbr","-cq","23","-b:v","0","-g","60","-r","60","-pix_fmt","yuv420p","-movflags","frag_keyframe+empty_moov","-f","mp4","-y",@"C:\rec\recording_part01.mp4"}, RecordingArguments.Gdigrab(nvenc), "gdigrab 60 fps arguments");
  Same(new[]{"-c:v","h264_mf","-rate_control","quality","-quality","75","-g","60"}, RecordingArguments.EncoderArguments(nvenc with {Encoder=new EncoderInfo("h264_mf","MF",false)}), "h264_mf quality arguments");
  Same(new[]{"-c:v","h264_amf","-quality","speed","-rc","cqp","-qp_i","23","-qp_p","23","-g","60"}, RecordingArguments.EncoderArguments(nvenc with {Encoder=EncoderProbe.Hardware[2]}), "amf arguments");
  Same(new[]{"-hide_banner","-nostats","-progress","pipe:1","-i",@"C:\rec\a_part01.mp4","-c","copy","-movflags","+faststart","-f","mp4","-y",@"C:\rec\a.mp4"}, RecordingArguments.Remux(@"C:\rec\a_part01.mp4",@"C:\rec\a.mp4"), "remux arguments");
  Same(new[]{"-hide_banner","-nostats","-progress","pipe:1","-f","concat","-safe","0","-i",@"C:\rec\a_segments.txt","-c","copy","-movflags","+faststart","-f","mp4","-y",@"C:\rec\a.mp4"}, RecordingArguments.Concat(@"C:\rec\a_segments.txt",@"C:\rec\a.mp4"), "concat arguments");
  Check(RecordingArguments.ConcatList(new[]{@"C:\rec\p1.mp4",@"C:\rec\it's.mp4"})=="ffconcat version 1.0\nfile 'C:/rec/p1.mp4'\nfile 'C:/rec/it'\\''s.mp4'\n", "concat list escaping");
  Same(new[]{"-hide_banner","-nostdin","-loglevel","error","-f","lavfi","-i","nullsrc=s=256x256:r=30","-t","1","-pix_fmt","yuv420p","-c:v","h264_qsv","-f","null","-"}, EncoderProbe.ProbeArguments("h264_qsv"), "encoder probe arguments");
  Check(EncoderProbe.Candidates(true).First().Name=="h264_nvenc" && EncoderProbe.Candidates(false).First().Name=="libx264" && EncoderProbe.Candidates(true).Count()==6, "encoder order honours hardware preference");
  Same(new[]{"-v","error","-print_format","json","-show_streams","-show_format",@"C:\v.mp4"}, Ffprobe.Arguments(@"C:\v.mp4"), "ffprobe arguments");

  // ffprobe JSON parsing without a binary.
  var probed=Ffprobe.Parse(@"C:\v.mp4","{\"streams\":[{\"codec_type\":\"video\",\"codec_name\":\"h264\",\"width\":1280,\"height\":720,\"r_frame_rate\":\"60000/1001\",\"avg_frame_rate\":\"60000/1001\",\"side_data_list\":[{\"rotation\":-90}]},{\"codec_type\":\"audio\",\"codec_name\":\"aac\"}],\"format\":{\"format_name\":\"mov,mp4,m4a,3gp,3g2,mj2\",\"duration\":\"12.5\",\"size\":\"123456\"}}");
  Check(probed.Width==1280 && probed.Height==720 && probed.FrameRate==FrameRate.Ntsc60 && probed.VideoCodec=="h264" && probed.AudioCodec=="aac" && probed.HasAudio && probed.Rotation==270 && probed.FileSize==123456 && Math.Abs(probed.Duration.TotalSeconds-12.5)<1e-9, "ffprobe json parsed");
  Console.WriteLine("PASS: Video settings, FrameRate, recording/remux/concat/probe arguments, ffprobe parsing.");

  // Live checks when ffmpeg is present (tools\ffmpeg via get-ffmpeg.ps1); skipped otherwise.
  var probe=FfmpegLocator.Probe();
  if(!probe.Ok){Console.WriteLine("SKIP: ffmpeg not found ("+probe.Error+"); live encode/remux/concat checks not run.");return;}
  Console.WriteLine("ffmpeg: "+probe);
  var runner=new FfmpegRunner();
  var encoder=EncoderProbe.ResolveAsync(runner,true).GetAwaiter().GetResult(); Check(encoder is not null,"an H.264 encoder works");
  string dir=Path.Combine(AppContext.BaseDirectory,"mediachecks"); Directory.CreateDirectory(dir);
  string seg1=Path.Combine(dir,"seg1.mp4"), seg2=Path.Combine(dir,"seg2.mp4"), remuxed=Path.Combine(dir,"remuxed.mp4"), joined=Path.Combine(dir,"joined.mp4"), list=Path.Combine(dir,"segments.txt");
  var live=new RecordingOptions{Region=new PixelRect(0,0,320,180),FrameRate=FrameRate.Ntsc30,Quality=75,Encoder=encoder!,OutputPath=seg1};
  string[] EncodeArgs(string outPath)=>FfmpegRunner.StandardPrefix.Concat(new[]{"-f","lavfi","-i","testsrc2=s=320x180:r=30000/1001","-t","2"}).Concat(RecordingArguments.EncoderArguments(live)).Concat(new[]{"-pix_fmt","yuv420p","-movflags","frag_keyframe+empty_moov","-f","mp4","-y",outPath}).ToArray();
  var progress=new Progress<FfmpegProgress>(_=>{});
  var r1=runner.RunAsync(EncodeArgs(seg1),progress).GetAwaiter().GetResult(); Check(r1.Succeeded,"fragmented encode with "+encoder!.Name+": "+r1.ErrorText());
  Check(r1.LastProgress is not null && r1.LastProgress.IsEnd && r1.LastProgress.Frame>=59 && r1.LastProgress.TotalSize>0 && r1.LastProgress.OutTime>TimeSpan.FromSeconds(1.9),"progress parsed: "+r1.LastProgress);
  var r2=runner.RunAsync(EncodeArgs(seg2)).GetAwaiter().GetResult(); Check(r2.Succeeded,"second segment");
  var info1=Ffprobe.ProbeAsync(seg1).GetAwaiter().GetResult(); Check(info1.Width==320 && info1.Height==180 && info1.FrameRate==FrameRate.Ntsc30 && info1.VideoCodec=="h264" && !info1.HasAudio,"probe of fragmented segment: "+info1);
  Check(runner.RunAsync(RecordingArguments.Remux(seg1,remuxed)).GetAwaiter().GetResult().Succeeded,"remux");
  var info2=Ffprobe.ProbeAsync(remuxed).GetAwaiter().GetResult(); Check(info2.FrameRate==FrameRate.Ntsc30 && Math.Abs(info2.Duration.TotalSeconds-2)<0.1,"remuxed file keeps 29.97 fps and 2 s: "+info2);
  File.WriteAllText(list,RecordingArguments.ConcatList(new[]{seg1,seg2}));
  Check(runner.RunAsync(RecordingArguments.Concat(list,joined)).GetAwaiter().GetResult().Succeeded,"concat");
  var info3=Ffprobe.ProbeAsync(joined).GetAwaiter().GetResult(); Check(Math.Abs(info3.Duration.TotalSeconds-4)<0.15 && info3.FrameRate==FrameRate.Ntsc30,"joined file is 4 s at 29.97: "+info3);
  using(var cts=new System.Threading.CancellationTokenSource(300)){var cancelled=runner.RunAsync(FfmpegRunner.StandardPrefix.Concat(new[]{"-re","-f","lavfi","-i","testsrc2=s=64x64","-t","60","-f","null","-"}).ToArray(),null,cts.Token).GetAwaiter().GetResult(); Check(cancelled.Cancelled,"cancellation kills ffmpeg");}
  var bad=runner.RunAsync(new[]{"-hide_banner","-i",Path.Combine(dir,"missing.mp4"),"-f","null","-"}).GetAwaiter().GetResult(); Check(!bad.Succeeded && bad.StderrTail.Count>0 && bad.ErrorText().Contains("missing.mp4"),"stderr tail kept on failure");
  Console.WriteLine($"PASS: live ffmpeg encode ({encoder.DisplayName}), progress, ffprobe, remux, concat, cancel, stderr tail.");
 }
}
