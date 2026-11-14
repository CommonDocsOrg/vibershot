using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vibershot.Core;
using Vibershot.Media;
using Vibershot.Gallery;
class Program {
 static void Check(bool ok, string what) { if (!ok) throw new Exception("FAIL: " + what); }
 static void Same(string[] expected, string[] actual, string what) { if (!expected.SequenceEqual(actual)) throw new Exception($"FAIL: {what}\n expected: {string.Join(" ", expected)}\n actual:   {string.Join(" ", actual)}"); }
 [STAThread] static void Main() {
  var cli=Environment.GetCommandLineArgs(); if(cli.Length>=3&&cli[1]=="--editor"){VideoEditorChecks.ShowEditor(cli[2],cli.Skip(3).ToArray());return;}
  BitmapSource Frame(int offset) { int w=120,h=100; byte[] p=new byte[w*h*4]; for(int y=0;y<h;y++)for(int x=0;x<w;x++){int i=(y*w+x)*4; p[i]=(byte)((x*17+(y+offset)*7)%256);p[i+1]=(byte)((x*3+(y+offset)*13)%256);p[i+2]=(byte)((x*11+(y+offset)*19)%256);p[i+3]=255;} var b=BitmapSource.Create(w,h,96,96,PixelFormats.Bgra32,null,p,w*4);b.Freeze();return b; }
  var first=Frame(0);var next=Frame(40);var stitched=ScrollStitcher.Append(first,first,next);if(stitched.PixelHeight!=140)throw new Exception("Wrong overlap");
  var expected=Frame(0); byte[] pixels=new byte[120*140*4];stitched.CopyPixels(pixels,480,0);byte[] tail=new byte[480*100];next.CopyPixels(tail,480,0);for(int i=0;i<480*40;i++)if(pixels[480*100+i]!=tail[480*60+i])throw new Exception("Wrong stitched pixels");
  bool rejected=false;try{ScrollStitcher.Append(first,first,first);}catch(InvalidOperationException){rejected=true;}if(!rejected)throw new Exception("Duplicate accepted");
  var path=Path.Combine(AppContext.BaseDirectory,"test.avi");File.Delete(path);using(var writer=new VideoWriter(path,120,100)){writer.AddFrame(first);using var recovery=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);byte[] checkpoint=new byte[checked((int)recovery.Length)];recovery.ReadExactly(checkpoint);if(BitConverter.ToInt32(checkpoint,4)!=checkpoint.Length-8||BitConverter.ToInt32(checkpoint,48)!=1)throw new Exception("Invalid recovery checkpoint");writer.AddFrame(next);}
  byte[] avi=File.ReadAllBytes(path);if(Encoding.ASCII.GetString(avi,0,4)!="RIFF"||BitConverter.ToInt32(avi,4)!=avi.Length-8||BitConverter.ToInt32(avi,48)!=2)throw new Exception("Invalid AVI header");
  int pos=12;while(pos<avi.Length){string id=Encoding.ASCII.GetString(avi,pos,4);int size=BitConverter.ToInt32(avi,pos+4);if(size<0||pos+8+size>avi.Length)throw new Exception("Invalid AVI chunk");pos+=8+size+(size&1);}if(pos!=avi.Length)throw new Exception("Bad AVI size");
  var config=AppConfig.FromJson("{\"Autosave\":true}")!;if(!config.ShowCaptureActions||config.AfterCapture.AskWhereToSave||!config.AfterCapture.SaveAsFile)throw new Exception("Legacy config defaults");
  if(new AppConfig().PrintScreen!=PrintScreenAction.CaptureTool||new AppConfig().SetupCompleted)throw new Exception("Fresh install: Print Screen opens the region tool and setup is pending");
  if(!config.SetupCompleted)throw new Exception("An existing config counts as set up");
  var ps=JsonSerializer.Deserialize<AppConfig>("{\"PrintScreen\":\"FullDesktop\"}")!;if(ps.PrintScreen!=PrintScreenAction.FullDesktop)throw new Exception("Print Screen setting not read");
  if(config.WebpQuality!=80)throw new Exception("WebP quality default");
  var legacyWebp=AppConfig.FromJson("{\"Format\":2,\"WebpQuality\":55}")!;if(legacyWebp.Format!=ImageFormat.Webp||legacyWebp.AfterCapture.Format!=ImageFormat.Webp||legacyWebp.WebpQuality!=55)throw new Exception("Legacy WebP enum value changed");
  SettingsChecks.Run();
  UpdateChecks.Run();
  foreach(var (fmt,lossless) in new[]{(ImageFormat.Webp,false),(ImageFormat.WebpLossless,true)}){
   var ms=new MemoryStream();ImageEncoding.Encode(first,fmt,60,ms);byte[] webp=ms.ToArray();
   if(webp.Length<12||Encoding.ASCII.GetString(webp,0,4)!="RIFF"||Encoding.ASCII.GetString(webp,8,4)!="WEBP")throw new Exception("Invalid WebP header for "+fmt);
   string chunk=Encoding.ASCII.GetString(webp,12,4);if(lossless?chunk!="VP8L":chunk!="VP8 ")throw new Exception("Unexpected WebP chunk "+chunk+" for "+fmt);
   using var decoded=SkiaSharp.SKBitmap.Decode(webp);if(decoded is null||decoded.Width!=120||decoded.Height!=100)throw new Exception("WebP size mismatch for "+fmt);
   if(lossless){byte[] orig=new byte[120*100*4];first.CopyPixels(orig,480,0);for(int y=0;y<100;y++)for(int x=0;x<120;x++){var c=decoded.GetPixel(x,y);int i=(y*120+x)*4;if(c.Blue!=orig[i]||c.Green!=orig[i+1]||c.Red!=orig[i+2]||c.Alpha!=orig[i+3])throw new Exception("Lossless WebP changed pixels");}}
   if(ImageEncoding.Extension(fmt)!="webp")throw new Exception("WebP extension");
  }

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

  GalleryChecks(first);
  EditorChecks.Run(first);
  VideoEditorChecks.Run();
  VideoSelectChecks.Run();
  Console.WriteLine("PASS: overlap and pixels, duplicate rejection, AVI frame counts/chunk sizes, legacy settings defaults, WebP lossy/lossless encoding, editor history/operations/project/flatten, gallery sidecars/index/query/thumbnails/files.");

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
  VideoEditorChecks.RunLive(runner,encoder,dir);
 }
 static void GalleryChecks(BitmapSource image) {
  // Legacy config without a Gallery section loads with defaults; a saved section round-trips.
  var legacy=AppConfig.FromJson("{\"Autosave\":true}")!;
  if(legacy.Gallery is null||legacy.Gallery.ThumbnailSize!=200||legacy.AfterCapture.OpenGallery||!legacy.Gallery.ShowDetailPane||legacy.Gallery.WindowWidth is not null)throw new Exception("Gallery config defaults");
  if(!legacy.Gallery.LibraryRoot.EndsWith("Vibershot",StringComparison.OrdinalIgnoreCase))throw new Exception("Legacy config keeps the AppData library root");
  if(!new AppConfig().Gallery.LibraryRoot.EndsWith("Vibershot Gallery",StringComparison.OrdinalIgnoreCase))throw new Exception("Fresh install library root under Pictures");
  var withGallery=AppConfig.FromJson("{\"Gallery\":{\"ThumbnailSize\":260,\"OpenGalleryAfterCapture\":true,\"ExtraFolders\":[\"C:\\\\Shots\"],\"WindowWidth\":1000.5}}")!;
  if(withGallery.Gallery.ThumbnailSize!=260||!withGallery.AfterCapture.OpenGallery||withGallery.Gallery.ExtraFolders.Count!=1||withGallery.Gallery.WindowWidth!=1000.5)throw new Exception("Gallery config not read");
  var roundTrip=AppConfig.FromJson(withGallery.ToJson())!;if(roundTrip.Gallery.ThumbnailSize!=260||roundTrip.Gallery.ExtraFolders[0]!="C:\\Shots"||!roundTrip.AfterCapture.OpenGallery)throw new Exception("Gallery config round trip");
  if(withGallery.ToJson().Contains("OpenGalleryAfterCapture"))throw new Exception("Legacy gallery flag must not be written back");

  var dir=Path.Combine(AppContext.BaseDirectory,"gallery-test");if(Directory.Exists(dir))Directory.Delete(dir,true);Directory.CreateDirectory(dir);
  // Sidecar round trip next to a real file.
  var png=Path.Combine(dir,"shot.png");using(var s=File.Create(png)){ImageEncoding.Encode(image,ImageFormat.Png,80,s);}
  var created=new DateTime(2026,11,12,9,30,15);
  var sidecar=CaptureSidecar.ForImage(image,CaptureKind.Window,"chrome","Docs - Google Chrome",created);sidecar.AddTag("Work");sidecar.AddTag("work");sidecar.AddTag("#bug");sidecar.Favourite=true;
  sidecar.Write(png);
  if(!File.Exists(CaptureSidecar.PathFor(png))||CaptureSidecar.PathFor(png)!=png+".vibe.json")throw new Exception("Sidecar path");
  if(!CaptureSidecar.IsSidecar(CaptureSidecar.PathFor(png))||CaptureSidecar.MediaPathFor(CaptureSidecar.PathFor(png))!=png)throw new Exception("Sidecar path helpers");
  var back=CaptureSidecar.Read(png)??throw new Exception("Sidecar not read");
  if(back.Kind!=CaptureKind.Window||back.SourceProcess!="chrome"||back.SourceTitle!="Docs - Google Chrome"||back.Width!=120||back.Height!=100||back.Created!=created||!back.Favourite||back.DurationSeconds is not null)throw new Exception("Sidecar fields");
  if(back.Tags.Count!=2||back.Tags[0]!="Work"||back.Tags[1]!="bug"||!back.HasTag("WORK"))throw new Exception("Sidecar tags: "+string.Join(",",back.Tags));
  if(!File.ReadAllText(CaptureSidecar.PathFor(png)).Contains("\"Window\""))throw new Exception("Sidecar enum should be a string");
  if(CaptureSidecar.Read(Path.Combine(dir,"missing.png")) is not null)throw new Exception("Missing sidecar should be null");
  File.WriteAllText(CaptureSidecar.PathFor(Path.Combine(dir,"corrupt.png")),"{not json");if(CaptureSidecar.Read(Path.Combine(dir,"corrupt.png")) is not null)throw new Exception("Corrupt sidecar should be null");
  var video=CaptureSidecar.ForVideo(640,360,12.5);video.Write(Path.Combine(dir,"clip.avi"));var videoBack=CaptureSidecar.Read(Path.Combine(dir,"clip.avi"))!;if(videoBack.Kind!=CaptureKind.Video||videoBack.DurationSeconds!=12.5)throw new Exception("Video sidecar");

  // Scan: files with and without sidecars; sidecars themselves and unknown types are not items.
  var plain=Path.Combine(dir,"plain.jpg");File.WriteAllBytes(plain,new byte[]{1,2,3});File.WriteAllBytes(Path.Combine(dir,"clip.avi"),new byte[]{4,5});File.WriteAllText(Path.Combine(dir,"notes.txt"),"x");
  var sub=Path.Combine(dir,"nested");Directory.CreateDirectory(sub);File.WriteAllBytes(Path.Combine(sub,"deep.webp"),new byte[]{6});
  var scanned=GalleryIndex.Scan(new[]{dir});
  if(scanned.Count!=4)throw new Exception("Scan count "+scanned.Count+": "+string.Join(",",scanned.Select(f=>Path.GetFileName(f.Path))));
  if(scanned.Any(f=>f.Path.EndsWith(".txt")||CaptureSidecar.IsSidecar(f.Path)))throw new Exception("Scan picked up non-media");
  if(scanned.First(f=>f.Path==png).Sidecar is null||scanned.First(f=>f.Path==plain).Sidecar is not null)throw new Exception("Scan sidecar merge");
  if(GalleryIndex.Dedupe(new[]{dir,sub,dir.ToUpperInvariant()}).Count!=1)throw new Exception("Dedupe nested folders");

  // Merge: add, update sidecar, remove.
  using var index=new GalleryIndex(Array.Empty<string>());int changes=0,added=0;index.Changed+=()=>changes++;index.ItemAdded+=_=>added++;
  index.Merge(scanned);
  if(index.Items.Count!=4||added!=4||changes!=1)throw new Exception("Merge add");
  var shot=index.Find(png)!;if(!shot.HasSidecar||shot.SourceProcess!="chrome"||shot.Created!=created||!shot.Favourite||shot.Width!=120||shot.Kind!=GalleryItemKind.Image)throw new Exception("Merged item with sidecar");
  var plainItem=index.Find(plain)!;if(plainItem.HasSidecar||plainItem.SourceProcess is not null||plainItem.Tags.Count!=0||plainItem.CaptureKind!=CaptureKind.Unknown||plainItem.Size!=3)throw new Exception("Merged item without sidecar");
  if(index.Find(Path.Combine(dir,"clip.avi"))!.Kind!=GalleryItemKind.Video||index.Find(Path.Combine(sub,"deep.webp"))!.Badge!="")throw new Exception("Merged kinds");
  index.Merge(scanned);if(changes!=1)throw new Exception("Unchanged merge raised Changed");
  var updated=scanned.Select(f=>f.Path==plain?f with{Sidecar=new CaptureSidecar{Kind=CaptureKind.Region,Tags={"later"}},SidecarModified=DateTime.Now}:f).Where(f=>f.Path!=png).ToList();
  index.Merge(updated);
  if(index.Items.Count!=3||index.Find(png) is not null||!plainItem.HasSidecar||plainItem.CaptureKind!=CaptureKind.Region||plainItem.TagsText!="later"||changes!=2)throw new Exception("Merge update/remove");

  // Search, filter, sort on an in-memory set.
  GalleryItem Item(string name,long size,DateTime when,CaptureSidecar? meta)=>new(new ScannedFile(Path.Combine(dir,name),size,when,when,meta));
  var t0=new DateTime(2026,11,10,8,0,0);
  var items=new List<GalleryItem>{
   Item("alpha.png",300,t0,new CaptureSidecar{Kind=CaptureKind.Window,SourceProcess="chrome",SourceTitle="Issue 42 - GitHub",Created=t0,Tags={"work"}}),
   Item("beta.mp4",900_000,t0.AddDays(1),new CaptureSidecar{Kind=CaptureKind.Video,DurationSeconds=65,Created=t0.AddDays(1),Favourite=true}),
   Item("gamma.webp",100,t0.AddDays(2),new CaptureSidecar{Kind=CaptureKind.Region,SourceProcess="Code",Created=t0.AddDays(2),Tags={"Work","bug"}}),
   Item("delta.vibeshot",5000,t0.AddHours(1),null),
   Item("Chrome window.jpg",200,t0.AddDays(-1),null)};
  List<string> Names(GalleryFilter f,string? tag,string? q,GallerySort s,bool desc=true)=>GalleryQuery.Apply(items,f,tag,q,s,desc).Select(i=>i.Name).ToList();
  if(string.Join("|",Names(GalleryFilter.All,null,null,GallerySort.Date))!="gamma.webp|beta.mp4|delta.vibeshot|alpha.png|Chrome window.jpg")throw new Exception("Date sort");
  if(string.Join("|",Names(GalleryFilter.All,null,null,GallerySort.Name,false))!="alpha.png|beta.mp4|Chrome window.jpg|delta.vibeshot|gamma.webp")throw new Exception("Name sort");
  if(Names(GalleryFilter.All,null,null,GallerySort.Size)[0]!="beta.mp4"||Names(GalleryFilter.All,null,null,GallerySort.Size,false)[0]!="gamma.webp")throw new Exception("Size sort");
  if(string.Join("|",Names(GalleryFilter.All,null,"chrome",GallerySort.Name,false))!="alpha.png|Chrome window.jpg")throw new Exception("Search by app/name");
  if(Names(GalleryFilter.All,null,"github 42",GallerySort.Date).Single()!="alpha.png")throw new Exception("Search by title with two words");
  if(Names(GalleryFilter.All,null,"bug",GallerySort.Date).Single()!="gamma.webp")throw new Exception("Search by tag");
  if(Names(GalleryFilter.All,null,"chrome bug",GallerySort.Date).Count!=0)throw new Exception("All words must match");
  if(Names(GalleryFilter.Images,null,null,GallerySort.Date).Count!=3||Names(GalleryFilter.Videos,null,null,GallerySort.Date).Single()!="beta.mp4"||Names(GalleryFilter.Projects,null,null,GallerySort.Date).Single()!="delta.vibeshot")throw new Exception("Kind filters");
  if(Names(GalleryFilter.Favourites,null,null,GallerySort.Date).Single()!="beta.mp4")throw new Exception("Favourites filter");
  if(string.Join("|",Names(GalleryFilter.Tag,"WORK",null,GallerySort.Name,false))!="alpha.png|gamma.webp")throw new Exception("Tag filter ignores case");
  var tags=GalleryQuery.Tags(items);if(tags.Count!=2||tags[0].Tag!="work"||tags[0].Count!=2||tags[1].Tag!="bug")throw new Exception("Tag counts: "+string.Join(",",tags));
  var today=new DateTime(2026,11,12);
  if(GalleryQuery.GroupLabel(items[2],GallerySort.Date,today)!="TODAY"||GalleryQuery.GroupLabel(items[1],GallerySort.Date,today)!="YESTERDAY"||GalleryQuery.GroupLabel(items[0],GallerySort.Date,today)!="TUESDAY 10 NOVEMBER")throw new Exception("Day groups");
  if(GalleryQuery.GroupLabel(items[4],GallerySort.Name,today)!="C"||GalleryQuery.GroupLabel(items[1],GallerySort.Size,today)!="100 KB TO 1 MB")throw new Exception("Name/size groups");
  if(items[1].Badge!="1:05"||items[0].Hint!="chrome"||GalleryItem.FormatSize(1536)!="2 KB"||GalleryItem.FormatDuration(3725)!="1:02:05")throw new Exception("Badge/hint formatting");

  // Thumbnail cache keys: stable for the same inputs, different for any change.
  var when=new DateTime(2026,11,12,10,0,0,DateTimeKind.Local);
  string k1=ThumbnailCache.KeyFor(@"C:\Shots\a.png",10,when),k2=ThumbnailCache.KeyFor(@"c:\shots\A.PNG",10,when);
  if(k1.Length!=40||k1.Any(c=>!Uri.IsHexDigit(c))||k1!=k2)throw new Exception("Thumbnail key stability");
  if(k1==ThumbnailCache.KeyFor(@"C:\Shots\a.png",11,when)||k1==ThumbnailCache.KeyFor(@"C:\Shots\a.png",10,when.AddSeconds(1))||k1==ThumbnailCache.KeyFor(@"C:\Shots\b.png",10,when))throw new Exception("Thumbnail key sensitivity");
  var thumbs=new ThumbnailCache(Path.Combine(dir,"thumbs"));
  if(ThumbnailCache.ProbeSize(png) is not (120,100))throw new Exception("Probe size");
  var small=ThumbnailCache.LoadImage(png,60)!;if(small.PixelWidth!=60||small.PixelHeight!=50||!small.IsFrozen)throw new Exception("Scaled decode "+small.PixelWidth+"x"+small.PixelHeight);
  var webp=Path.Combine(dir,"big.webp");using(var s=File.Create(webp)){ImageEncoding.Encode(image,ImageFormat.WebpLossless,100,s);}
  var smallWebp=ThumbnailCache.LoadImage(webp,30)!;if(smallWebp.PixelWidth!=30||smallWebp.PixelHeight!=25)throw new Exception("WebP scaled decode "+smallWebp.PixelWidth+"x"+smallWebp.PixelHeight);
  var tileItem=new GalleryItem(GalleryIndex.Describe(png)!);thumbs.Request(tileItem);
  for(int i=0;i<100&&tileItem.Thumbnail is null;i++)System.Threading.Thread.Sleep(50);
  if(tileItem.Thumbnail is null||!tileItem.Thumbnail.IsFrozen||tileItem.Thumbnail.PixelWidth!=120||!File.Exists(thumbs.CachePathFor(tileItem)))throw new Exception("Thumbnail generation/cache file");
  var film=new FilmTileThumbnailer().CreateThumbnail(Path.Combine(dir,"clip.avi"),320)!;if(film.PixelWidth!=320||!film.IsFrozen)throw new Exception("Film tile");

  // Rename moves the sidecar; delete removes it (Recycle Bin is exercised by hand).
  var renamed=GalleryFiles.Rename(shot,"renamed");if(renamed!=Path.Combine(dir,"renamed.png")||!File.Exists(renamed)||!File.Exists(CaptureSidecar.PathFor(renamed))||File.Exists(png))throw new Exception("Rename with sidecar");
  if(GalleryFiles.UniquePath(renamed)!=Path.Combine(dir,"renamed (2).png"))throw new Exception("Unique path");
  var imported=GalleryFiles.Import(renamed,Path.Combine(dir,"import"))!;if(CaptureSidecar.Read(imported)?.SourceProcess!="chrome")throw new Exception("Import keeps sidecar");
  var importedPlain=GalleryFiles.Import(plain,Path.Combine(dir,"import"))!;if(CaptureSidecar.Read(importedPlain)?.Kind!=CaptureKind.Imported)throw new Exception("Import writes sidecar");

  // Library cleanup: only the library's own folders, age from the sidecar, sidecars go with the media, projects and subfolders untouched.
  var lib=new GallerySettings{LibraryRoot=Path.Combine(dir,"lib")};lib.Cleanup.Enabled=true;lib.Cleanup.Screenshots=true;lib.Cleanup.ScreenshotDays=30;lib.Cleanup.Recordings=true;lib.Cleanup.RecordingDays=14;lib.Cleanup.Recovery=true;lib.Cleanup.RecoveryDays=7;
  var now=new DateTime(2026,11,12,12,0,0);string recov=Path.Combine(dir,"recovery");
  Directory.CreateDirectory(lib.ScreenshotsFolder);Directory.CreateDirectory(lib.RecordingsFolder);Directory.CreateDirectory(lib.ProjectsFolder);Directory.CreateDirectory(recov);Directory.CreateDirectory(Path.Combine(lib.ScreenshotsFolder,"keep"));
  void Shot(string path,DateTime when){File.WriteAllBytes(path,new byte[]{1});new CaptureSidecar{Kind=CaptureKind.Region,Created=when}.Write(path);}
  Shot(Path.Combine(lib.ScreenshotsFolder,"old.png"),now.AddDays(-31));Shot(Path.Combine(lib.ScreenshotsFolder,"new.png"),now.AddDays(-29));Shot(Path.Combine(lib.ScreenshotsFolder,"old.vibeshot"),now.AddDays(-400));Shot(Path.Combine(lib.ScreenshotsFolder,"keep","old.png"),now.AddDays(-400));
  Shot(Path.Combine(lib.RecordingsFolder,"old.mp4"),now.AddDays(-15));Shot(Path.Combine(lib.RecordingsFolder,"new.mp4"),now.AddDays(-13));Shot(Path.Combine(lib.ProjectsFolder,"old.vibeshot"),now.AddDays(-400));
  var oldRecovery=Path.Combine(recov,"old.mp4");File.WriteAllBytes(oldRecovery,new byte[]{1});File.SetCreationTime(oldRecovery,now.AddDays(-8));var newRecovery=Path.Combine(recov,"new.mp4");File.WriteAllBytes(newRecovery,new byte[]{1});File.SetCreationTime(newRecovery,now.AddDays(-6));
  var removed=new List<string>();var report=LibraryCleanup.Run(lib,recov,now,p=>{removed.Add(Path.GetFileName(Path.GetDirectoryName(p)!)+"/"+Path.GetFileName(p));File.Delete(p);});
  if(report.Screenshots!=1||report.Recordings!=1||report.Recovery!=1||report.Failed!=0||report.Count!=3)throw new Exception("Cleanup report: "+report);
  if(!removed.SequenceEqual(new[]{"Screenshots/old.png","Recordings/old.mp4","recovery/old.mp4"}))throw new Exception("Cleanup removed: "+string.Join(",",removed));
  if(File.Exists(CaptureSidecar.PathFor(Path.Combine(lib.ScreenshotsFolder,"old.png")))||!File.Exists(CaptureSidecar.PathFor(Path.Combine(lib.ScreenshotsFolder,"new.png"))))throw new Exception("Cleanup sidecars");
  if(!report.Summary.Contains("1 screenshot, 1 recording, 1 recovery file"))throw new Exception("Cleanup summary: "+report.Summary);
  var failing=LibraryCleanup.Run(lib,recov,now.AddDays(100),p=>throw new IOException("locked"));if(failing.Failed!=3||failing.Count!=0||!failing.Summary.Contains("3 could not be moved"))throw new Exception("Cleanup failures counted: "+failing);
  var cfg=new AppConfig{Gallery=lib};cfg.Gallery.Cleanup.LastRun=now.AddHours(-2);if(LibraryCleanup.RunIfDue(cfg,now) is not null)throw new Exception("Cleanup should wait a day between passes");
  cfg.Gallery.Cleanup.Enabled=false;cfg.Gallery.Cleanup.LastRun=null;if(LibraryCleanup.RunIfDue(cfg,now) is not null)throw new Exception("Cleanup off runs nothing");
  if(new CleanupReport(0,0,0,0).Summary!="Library cleanup: nothing to remove"||CleanupSettings.ClampDays(0)!=1||CleanupSettings.ClampDays(99999)!=3650)throw new Exception("Cleanup helpers");
  var defaults=GalleryIndex.DefaultFolders(cfg);if(defaults.Any(f=>f.Contains("Captures"))||!defaults.Contains(lib.ScreenshotsFolder)||!defaults.Contains(lib.ProjectsFolder)||!defaults.Contains(GalleryIndex.RecoveryFolder))throw new Exception("Gallery folders: "+string.Join("|",defaults));
  cfg.AfterCapture.Destinations.Add(new SaveDestination{Path=Path.Combine(dir,"custom")});if(GalleryIndex.DefaultFolders(cfg).Any(f=>f.EndsWith("custom")))throw new Exception("Custom save folders are not watched");
  cfg.Gallery.ExtraFolders.Add(Path.Combine(dir,"custom"));if(!GalleryIndex.DefaultFolders(cfg).Any(f=>f.EndsWith("custom")))throw new Exception("Extra folders are watched");
  Directory.Delete(dir,true);

 }
}
static class SettingsChecks {
 static void Check(bool ok,string what){if(!ok)throw new Exception("FAIL: "+what);}
 public static void Run(){
  // Fresh install defaults: everything in sections, projects as the capture format, library under Pictures.
  var fresh=new AppConfig();
  Check(fresh.AfterCapture.SaveAsFile&&fresh.AfterCapture.SaveToLibrary&&!fresh.AfterCapture.AskWhereToSave&&fresh.AfterCapture.Format==ImageFormat.Vibeshot&&fresh.AfterCapture.CopyToClipboard&&!fresh.AfterCapture.OpenGallery&&!fresh.AfterCapture.OpenEditor&&fresh.AfterCapture.ShowPreview,"After capture defaults");
  Check(fresh.Hotkeys.CaptureRegion=="Ctrl+Shift+S"&&fresh.Hotkeys.FullDesktop==""&&fresh.Hotkeys.Gesture(HotkeyAction.OpenGallery).IsEmpty,"Hotkey defaults");
  Check(!fresh.Gallery.Cleanup.Enabled&&fresh.Gallery.Cleanup.ScreenshotDays==30&&fresh.Gallery.Cleanup.RecoveryDays==7,"Cleanup defaults");
  string json=fresh.ToJson();
  Check(json.Contains("\"AfterCapture\"")&&json.Contains("\"Hotkeys\"")&&json.Contains("\"Cleanup\"")&&json.Contains("\"PrintScreen\": \"CaptureTool\"")&&!json.Contains("\"Autosave\"")&&!json.Contains("\"OutputFolder\"")&&!json.Contains("HotkeyDescription"),"Fresh config writes sections only: "+json);
  var back=AppConfig.FromJson(json)!;Check(back.AfterCapture.Format==ImageFormat.Vibeshot&&back.SetupCompleted==false&&back.Gallery.LibraryRoot==fresh.Gallery.LibraryRoot,"Fresh config round trip keeps SetupCompleted false");

  // A pre-section config: flat fields fold into AfterCapture, the custom folder becomes a destination and stays visible in the gallery.
  var old=AppConfig.FromJson("{\"OutputFolder\":\"D:\\\\Captures\",\"ShowCaptureActions\":false,\"ShowSaveDialog\":false,\"Autosave\":false,\"CopyToClipboard\":false,\"Format\":1,\"WebpQuality\":70,\"HotkeyDescription\":\"Ctrl+Alt+P\",\"PrintScreen\":\"Off\",\"Editor\":{\"OpenEditorAfterCapture\":true},\"Gallery\":{\"LibraryRoot\":\"C:\\\\Lib\",\"OpenGalleryAfterCapture\":true}}")!;
  Check(!old.AfterCapture.SaveAsFile&&!old.AfterCapture.CopyToClipboard&&old.AfterCapture.Format==ImageFormat.Jpeg&&old.AfterCapture.WebpQuality==70&&old.AfterCapture.OpenEditor&&old.AfterCapture.OpenGallery,"Legacy flags migrate");
  Check(!old.AfterCapture.SaveToLibrary&&old.AfterCapture.Destinations.Count==1&&old.AfterCapture.Destinations[0].Path=="D:\\Captures"&&old.AfterCapture.Destinations[0].Enabled,"Legacy output folder becomes a destination");
  Check(old.Gallery.ExtraFolders.Contains("D:\\Captures")&&old.Gallery.LibraryRoot=="C:\\Lib","Legacy output folder is watched; explicit library root kept");
  Check(old.Hotkeys.CaptureRegion=="Ctrl+Alt+P"&&old.PrintScreen==PrintScreenAction.Off&&old.SetupCompleted&&!old.ShowCaptureActions,"Legacy hotkey, Print Screen and setup state");
  Check(old.OutputFolder=="C:\\Lib\\Screenshots"&&old.AfterCapture.EnabledFolders(old.Gallery).Count==0,"Save off writes nowhere even with Open in gallery on");
  old.AfterCapture.SaveAsFile=true;Check(old.AfterCapture.EnabledFolders(old.Gallery).SequenceEqual(new[]{"C:\\Lib\\Screenshots","D:\\Captures"}),"Open in gallery keeps a library copy next to the legacy folder");
  old.AfterCapture.OpenGallery=false;Check(old.OutputFolder=="D:\\Captures","OutputFolder view points at the first destination when the library is off");
  string migrated=old.ToJson();Check(!migrated.Contains("\"OutputFolder\"")&&!migrated.Contains("\"Autosave\"")&&!migrated.Contains("OpenEditorAfterCapture")&&migrated.Contains("\"SetupCompleted\": true"),"Migrated config drops legacy fields: "+migrated);
  var sameFolder=AppConfig.FromJson("{\"OutputFolder\":\""+Path.Combine(GallerySettings.LegacyLibraryRoot,"Screenshots").Replace("\\","\\\\")+"\",\"Autosave\":true}")!;
  Check(sameFolder.AfterCapture.SaveToLibrary&&sameFolder.AfterCapture.Destinations.Count==0&&sameFolder.Gallery.ExtraFolders.Count==0,"Legacy default folder maps to the library");
  var sectioned=AppConfig.FromJson("{\"AfterCapture\":{\"SaveAsFile\":false},\"OutputFolder\":\"D:\\\\Old\"}")!;Check(!sectioned.AfterCapture.SaveAsFile&&sectioned.AfterCapture.Destinations.Count==0,"A config with sections ignores leftover legacy fields");

  // Destinations: any mix of library and custom folders, deduplicated; Open in gallery forces the library copy.
  var g=new GallerySettings{LibraryRoot="C:\\Lib"};var ac=new AfterCaptureSettings{SaveToLibrary=true,Destinations={new SaveDestination{Path="D:\\A"},new SaveDestination{Path="D:\\B",Enabled=false},new SaveDestination{Path="c:\\lib\\Screenshots\\"},new SaveDestination{Path="D:\\A\\"}}};
  var folders=ac.EnabledFolders(g);Check(folders.Count==2&&folders[0]=="C:\\Lib\\Screenshots"&&folders[1]=="D:\\A","Enabled folders: "+string.Join("|",folders));
  ac.SaveToLibrary=false;Check(ac.EnabledFolders(g).Count==2&&ac.EnabledFolders(g)[0]=="D:\\A","A custom destination that is the library folder still counts when the library switch is off");
  ac.Destinations.RemoveAt(2);Check(ac.EnabledFolders(g).Single()=="D:\\A"&&ac.PrimaryFolder(g)=="D:\\A","Library off leaves the custom folder");
  ac.OpenGallery=true;Check(ac.EnabledFolders(g)[0]=="C:\\Lib\\Screenshots"&&ac.PrimaryFolder(g)=="C:\\Lib\\Screenshots","Open in gallery keeps the library copy");
  ac.OpenGallery=false;ac.Destinations.Clear();Check(ac.PrimaryFolder(g)=="C:\\Lib\\Screenshots","No destinations falls back to the library");
  ac.SaveAsFile=false;Check(ac.EnabledFolders(g).Count==0,"Save off writes nowhere");ac.SaveAsFile=true;ac.AskWhereToSave=true;Check(ac.EnabledFolders(g).Count==0,"Ask each time writes nowhere automatically");
  Check(AfterCaptureSettings.SameFolder("C:\\Lib\\","c:/lib")&&!AfterCaptureSettings.SameFolder("C:\\Lib","C:\\Lib2"),"SameFolder");
  Check(!g.AddExtraFolder("C:\\Lib\\Projects")&&g.AddExtraFolder("D:\\Watch")&&!g.AddExtraFolder("d:\\watch\\")&&g.ExtraFolders.Count==1,"AddExtraFolder dedupes and skips the library");

  // Presets set the four switches; a matching mix reports its name, anything else is custom.
  var p=new AfterCaptureSettings();Check(p.MatchingPreset()=="Save to library","Default mix is the Save to library preset");
  Check(p.ApplyPreset("Quick copy")&&!p.SaveAsFile&&p.CopyToClipboard&&p.MatchingPreset()=="Quick copy","Quick copy preset");
  Check(p.ApplyPreset("Save and edit")&&p.SaveAsFile&&p.SaveToLibrary&&!p.CopyToClipboard&&p.OpenEditor&&p.MatchingPreset()=="Save and edit","Save and edit preset");
  Check(p.ApplyPreset("Save and review")&&p.OpenGallery&&!p.OpenEditor&&p.MatchingPreset()=="Save and review","Save and review preset");
  p.OpenEditor=true;Check(p.MatchingPreset()==null&&!p.ApplyPreset("nope"),"Custom mix has no preset");

  // Hotkey gestures: parsing, canonical text, validity and conflicts.
  Check(HotkeyGesture.TryParse("ctrl + shift + s",out var gs)&&gs.Modifiers==(HotkeyModifiers.Control|HotkeyModifiers.Shift)&&gs.Key=="S"&&gs.ToString()=="Ctrl+Shift+S"&&gs.IsValid,"Gesture parse");
  Check(HotkeyGesture.Parse("Win+PrtScn").Key=="PrintScreen"&&HotkeyGesture.Parse("Win+PrtScn").ToString()=="Win+PrtScn"&&HotkeyGesture.Parse("Alt+5").Key=="D5"&&HotkeyGesture.Parse("Alt+5").ToString()=="Alt+5","Gesture aliases");
  Check(HotkeyGesture.Parse("F11").IsValid&&HotkeyGesture.Parse("PrintScreen").IsValid&&!HotkeyGesture.Parse("S").IsValid&&!HotkeyGesture.Parse("Ctrl+Shift").IsValid&&HotkeyGesture.Parse("").IsEmpty&&HotkeyGesture.Parse(null).IsEmpty,"Gesture validity");
  Check(!HotkeyGesture.TryParse("Bogus+S",out _)&&HotkeyGesture.Parse("Ctrl+Escape").ToString()=="Ctrl+Esc"&&HotkeyGesture.Parse("Ctrl+pgdn").Key=="PageDown","Gesture errors and friendly names");
  var hk=new HotkeySettings{FullDesktop="Ctrl+Shift+D",OpenGallery="ctrl+shift+d"};
  Check(hk.ConflictFor(HotkeyAction.OpenEditor,HotkeyGesture.Parse("Ctrl+Shift+D"))==HotkeyAction.FullDesktop&&hk.ConflictFor(HotkeyAction.FullDesktop,HotkeyGesture.Parse("Ctrl+Shift+D"))==HotkeyAction.OpenGallery&&hk.ConflictFor(HotkeyAction.OpenEditor,HotkeyGesture.Parse("Ctrl+Shift+E"))==null,"Hotkey conflicts");
  hk[HotkeyAction.RecordVideo]="Ctrl+Shift+R";Check(hk.RecordVideo=="Ctrl+Shift+R"&&hk[HotkeyAction.RecordVideo]=="Ctrl+Shift+R"&&HotkeySettings.Label(HotkeyAction.CaptureRegion)=="Capture region or window","Hotkey indexer");
  // Print Screen held elsewhere: name the likely holder from the Windows setting and running apps.
  var sus=PrintScreenConflict.Identify(false,new[]{"explorer","onedrive","OneDrive","Snagit32","SnagitCapture"});
  Check(sus.Count==2&&sus[0].Name=="OneDrive"&&sus[1].Name=="Snagit"&&PrintScreenConflict.Names(sus)=="OneDrive or Snagit"&&PrintScreenConflict.Describe(sus).StartsWith("Print Screen is held by OneDrive or Snagit. In OneDrive Settings"),"Print Screen suspects from running apps");
  sus=PrintScreenConflict.Identify(true,new[]{"ShareX"});Check(sus.Count==2&&sus[0]==PrintScreenConflict.WindowsSnipping&&sus[0].SettingsUri!=null&&sus[1].Name=="ShareX","Windows setting is listed first");
  sus=PrintScreenConflict.Identify(false,new[]{"OneDrive","Greenshot"},altPrintScreenTaken:false);Check(sus.Count==1&&sus[0].Name=="Greenshot","OneDrive is cleared when Alt+Print Screen is free");
  sus=PrintScreenConflict.Identify(false,new[]{"explorer","notepad"});Check(sus.Count==0&&PrintScreenConflict.Names(sus)=="another app"&&PrintScreenConflict.Describe(sus).Contains("another app"),"Unknown Print Screen holder");
  Check(ImageEncoding.Extension(ImageFormat.Vibeshot)=="vibeshot"&&!ImageEncoding.IsFlat(ImageFormat.Vibeshot)&&ImageEncoding.IsFlat(ImageFormat.Png),"Vibeshot format facts");
  bool threw=false;try{ImageEncoding.Encode(BitmapSource.Create(1,1,96,96,PixelFormats.Bgra32,null,new byte[4],4),ImageFormat.Vibeshot,80,new MemoryStream());}catch(NotSupportedException){threw=true;}Check(threw,"Encoder refuses the project format");
  var dir=Path.Combine(AppContext.BaseDirectory,"nextpath");if(Directory.Exists(dir))Directory.Delete(dir,true);var when=new DateTime(2026,11,12,9,30,15);
  var p1=FileSaveService.NextPath(dir,ImageFormat.Vibeshot,when);Check(p1.EndsWith("screenshot_20261112_093015_0000000.vibeshot")&&Directory.Exists(dir),"NextPath name");File.WriteAllText(p1,"x");
  Check(FileSaveService.NextPath(dir,ImageFormat.Vibeshot,when).EndsWith(" (2).vibeshot"),"NextPath avoids collisions");Directory.Delete(dir,true);
  Console.WriteLine("PASS: settings sections, legacy migration, destinations, presets, hotkey gestures.");
 }
}
static class UpdateChecks {
 static void Check(bool ok,string what){if(!ok)throw new Exception("FAIL: "+what);}
 public static void Run(){
  // Version order: numeric parts, missing parts are zero, a pre-release tag sorts below the release.
  Check(AppVersion.Compare("0.1.0","0.1.0")==0&&AppVersion.Compare("0.1","0.1.0")==0&&AppVersion.Compare("v0.2.0","0.1.9+abc")>0&&AppVersion.Compare("0.10.0","0.9.0")>0,"Version numeric order");
  Check(AppVersion.Compare("1.0.0-beta.1","1.0.0")<0&&AppVersion.Compare("1.0.0-beta.2","1.0.0-beta.10")<0&&AppVersion.Compare("1.0.0-rc","1.0.0-beta")>0&&AppVersion.Compare("1.0.0-beta","1.0.0-beta")==0,"Version pre-release order");
  Check(AppVersion.IsNewer("0.2.0","0.1.0")&&!AppVersion.IsNewer("0.1.0","0.1.0")&&!AppVersion.IsNewer("0.1.0","0.1.1"),"IsNewer");
  Check(AppVersion.IsValid("1.2.3")&&AppVersion.IsValid("v1.2.3-beta.1+sha")&&!AppVersion.IsValid("")&&!AppVersion.IsValid("abc")&&!AppVersion.IsValid("1.x")&&AppVersion.Normalize(" v1.2.3+abc ")=="1.2.3","Version validity and normalising");
  Check(AppVersion.IsValid(AppVersion.Current)&&AppVersion.Current==AppVersion.Normalize(AppVersion.Current),"Running version is well formed: "+AppVersion.Current);

  // The feed written by tools\build-installer.ps1.
  string sha=new string('a',64);
  var m=UpdateManifest.Parse("{\"version\":\"0.2.0\",\"installerUrl\":\"https://example.test/VibershotSetup-0.2.0.exe\",\"sha256\":\""+sha+"\",\"size\":123,\"publishedUtc\":\"2026-11-12T10:00:00Z\",\"releaseUrl\":\"https://example.test/r\",\"notes\":\"Fixes\"}");
  Check(m.Version=="0.2.0"&&m.Size==123&&m.Notes=="Fixes"&&m.FileName=="VibershotSetup-0.2.0.exe"&&m.PublishedUtc!=null&&m.MinimumVersion==null,"Manifest parse");
  Check(UpdateManifest.Parse("{\"Version\":\"v0.3.0\",\"InstallerUrl\":\"x\",\"Sha256\":\""+sha.ToUpperInvariant()+"\"}").FileName=="VibershotSetup-0.3.0.exe","Manifest is case-insensitive and strips v");
  foreach(var bad in new[]{"{}","{\"version\":\"nope\",\"installerUrl\":\"x\",\"sha256\":\""+sha+"\"}","{\"version\":\"1.0.0\",\"sha256\":\""+sha+"\"}","{\"version\":\"1.0.0\",\"installerUrl\":\"x\",\"sha256\":\"abc\"}"}){
   bool threw=false;try{UpdateManifest.Parse(bad);}catch(Exception){threw=true;}Check(threw,"Manifest rejects "+bad);
  }
  Check(UpdateService.NeedsManualInstall(new UpdateManifest{MinimumVersion="0.2.0"},"0.1.0")&&!UpdateService.NeedsManualInstall(new UpdateManifest{MinimumVersion="0.1.0"},"0.1.0")&&!UpdateService.NeedsManualInstall(m,"0.0.1"),"Minimum version gate");

  // Check settings: due once a day when on, never when off; a check from a local feed path.
  var us=new UpdateSettings();var now=new DateTime(2026,11,12,12,0,0,DateTimeKind.Utc);
  Check(us.IsCheckDue(now)&&us.CheckAutomatically&&us.LastCheckUtc==null&&us.SkippedVersion==null&&us.FeedUrl==null,"Fresh settings are due");
  us.LastCheckUtc=now.AddHours(-3);Check(!us.IsCheckDue(now),"Recent check is not due");us.LastCheckUtc=now.AddHours(-25);Check(us.IsCheckDue(now),"Day-old check is due");
  us.CheckAutomatically=false;Check(!us.IsCheckDue(now),"Automatic off is never due");
  string dir=Path.Combine(AppContext.BaseDirectory,"updatechecks");if(Directory.Exists(dir))Directory.Delete(dir,true);Directory.CreateDirectory(dir);
  string fakeSetup=Path.Combine(dir,"VibershotSetup-9.9.9.exe");File.WriteAllBytes(fakeSetup,new byte[]{1,2,3,4,5});
  string realSha=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(fakeSetup))).ToLowerInvariant();
  Check(UpdateService.VerifySha256(fakeSetup,realSha)&&UpdateService.VerifySha256(fakeSetup,realSha.ToUpperInvariant())&&!UpdateService.VerifySha256(fakeSetup,sha)&&!UpdateService.VerifySha256(Path.Combine(dir,"missing.exe"),realSha),"SHA-256 verification");
  string feed=Path.Combine(dir,"latest.json");
  File.WriteAllText(feed,"{\"version\":\"9.9.9\",\"installerUrl\":\""+fakeSetup.Replace("\\","\\\\")+"\",\"sha256\":\""+realSha+"\",\"size\":5}");
  var settings=new UpdateSettings{FeedUrl=feed};var result=UpdateService.CheckAsync(settings).GetAwaiter().GetResult();
  Check(result.Available&&result.Error==null&&result.Manifest!.Version=="9.9.9"&&settings.LastCheckUtc!=null,"Local feed reports the newer version: "+result.Error);
  File.WriteAllText(feed,"{\"version\":\"0.0.1\",\"installerUrl\":\"x\",\"sha256\":\""+sha+"\"}");
  result=UpdateService.CheckAsync(settings).GetAwaiter().GetResult();Check(!result.Available&&result.Error==null&&result.Manifest!=null,"Older feed version is not offered");
  settings.FeedUrl=Path.Combine(dir,"nope.json");result=UpdateService.CheckAsync(settings).GetAwaiter().GetResult();Check(!result.Available&&result.Error!=null,"Missing feed is an error, not a throw");
  File.WriteAllText(feed,"not json");settings.FeedUrl=feed;Check(UpdateService.CheckAsync(settings).GetAwaiter().GetResult().Error!=null,"Bad feed is an error");
  // Download from a local path lands in the Updates folder and is verified; a wrong hash is refused and leaves nothing behind.
  var okManifest=new UpdateManifest{Version="9.9.9",InstallerUrl=fakeSetup,Sha256=realSha,Size=5};
  string downloaded=UpdateService.DownloadAsync(okManifest).GetAwaiter().GetResult();
  Check(File.Exists(downloaded)&&downloaded.StartsWith(UpdateService.DownloadFolder)&&Path.GetFileName(downloaded)=="VibershotSetup-9.9.9.exe","Download to the Updates folder: "+downloaded);
  Check(UpdateService.DownloadAsync(okManifest).GetAwaiter().GetResult()==downloaded,"A verified download is reused");
  bool refused=false;try{UpdateService.DownloadAsync(new UpdateManifest{Version="9.9.8",InstallerUrl=fakeSetup,Sha256=sha}).GetAwaiter().GetResult();}catch(InvalidDataException){refused=true;}
  Check(refused&&!File.Exists(Path.Combine(UpdateService.DownloadFolder,"VibershotSetup-9.9.8.exe"))&&!File.Exists(Path.Combine(UpdateService.DownloadFolder,"VibershotSetup-9.9.8.exe.part")),"Wrong checksum is refused and cleaned up");
  File.Delete(downloaded);
  string stale=Path.Combine(UpdateService.DownloadFolder,"VibershotSetup-0.0.1.exe");File.WriteAllBytes(stale,new byte[]{1});UpdateService.Cleanup();Check(!File.Exists(stale),"Cleanup removes installers that are not newer");

  // Installer seed: applied to a fresh config only, library root must be absolute, and the welcome page learns the installer handled the rest.
  var seed=InstallChoices.Parse("{\"libraryRoot\":\"D:\\\\Shots\\\\Lib\",\"installer\":\"inno\",\"version\":\"0.2.0\"}")!;
  var cfg=new AppConfig();Check(seed.ApplyTo(cfg)&&cfg.Gallery.LibraryRoot=="D:\\Shots\\Lib"&&cfg.InstalledByInstaller&&!cfg.SetupCompleted,"Seed applies the library and marks the installer");
  var relative=new AppConfig();Check(!InstallChoices.Parse("{\"libraryRoot\":\"Shots\"}")!.ApplyTo(relative)&&relative.Gallery.LibraryRoot==new AppConfig().Gallery.LibraryRoot&&relative.InstalledByInstaller,"Relative seed root is ignored");
  Check(InstallChoices.Parse("garbage")==null&&InstallChoices.TryRead(Path.Combine(dir,"none.json"))==null,"Bad or missing seed reads as null");
  string seedPath=Path.Combine(dir,"install-choices.json");File.WriteAllText(seedPath,"\uFEFF{\"libraryRoot\":\"D:\\\\Seeded\"}");
  Check(InstallChoices.TryRead(seedPath)!.LibraryRoot=="D:\\Seeded","Seed with a UTF-8 BOM reads");
  string roundTrip=cfg.ToJson();var back=AppConfig.FromJson(roundTrip)!;Check(back.InstalledByInstaller&&back.Gallery.LibraryRoot=="D:\\Shots\\Lib"&&back.Updates.CheckAutomatically&&roundTrip.Contains("\"Updates\""),"InstalledByInstaller and Updates persist");
  Check(AppConfig.FromJson("{\"Autosave\":true}")!.Updates.CheckAutomatically&&JsonSerializer.Deserialize<AppConfig>("{\"Updates\":null}")!.Updates!=null,"Legacy config gets update defaults");
  var skipped=AppConfig.FromJson("{\"Updates\":{\"CheckAutomatically\":false,\"SkippedVersion\":\"0.2.0\",\"FeedUrl\":\"C:\\\\feed.json\"}}")!;Check(!skipped.Updates.CheckAutomatically&&skipped.Updates.SkippedVersion=="0.2.0"&&skipped.Updates.FeedUrl=="C:\\feed.json","Update settings round trip");
  Directory.Delete(dir,true);
  Console.WriteLine("PASS: version order, update feed, local download and checksum, installer seed.");
 }
}
static class EditorChecks {
 static void Check(bool ok,string what){if(!ok)throw new Exception(what);}
 public static void Run(BitmapSource image){
  // Legacy config without an Editor section gets the defaults; a saved section reads back.
  var legacy=AppConfig.FromJson("{\"Autosave\":true}")!;Check(legacy.Editor.DefaultFontFamily=="Segoe UI"&&legacy.Editor.DefaultFontSize==18&&legacy.Editor.DefaultStrokeColor=="#F0612A"&&legacy.Editor.DefaultStrokeWidth==3&&!legacy.AfterCapture.OpenEditor,"Editor settings defaults");
  var editorCfg=AppConfig.FromJson("{\"Editor\":{\"OpenEditorAfterCapture\":true,\"DefaultStrokeWidth\":5}}")!;Check(editorCfg.AfterCapture.OpenEditor&&editorCfg.Editor.DefaultStrokeWidth==5&&!editorCfg.ToJson().Contains("OpenEditorAfterCapture"),"Editor settings not read");
  var cfgJson=JsonSerializer.Serialize(new AppConfig());Check(cfgJson.Contains("\"Editor\""),"Editor settings not written");

  // History: undo/redo, coalescing, rollback, depth limit, dirty tracking.
  var h=new Vibershot.Editing.History<string>("a",limit:3);
  h.Push("b");h.Push("c","drag");h.Push("d","drag");h.Push("e","drag");
  Check(h.Current=="e"&&h.UndoDepth==2,"Coalesced pushes should form one step");
  Check(h.Undo()=="b","Undo of a coalesced drag should restore the pre-drag state");
  Check(h.Redo()=="e","Redo should restore the end of the drag");
  h.BreakCoalescing();h.Push("f","drag");Check(h.UndoDepth==3,"Breaking coalescing should start a new step");
  h.Push("g");Check(h.UndoDepth==3,"Depth limit not enforced");
  Check(h.Undo()=="f"&&h.Undo()=="e"&&h.Undo()=="b"&&!h.CanUndo,"Oldest entry should have been dropped");
  Check(h.Redo()=="e"&&h.CanRedo,"Redo chain broken");
  h.Push("x");Check(!h.CanRedo,"Push should clear redo");
  h.Push("y","nudge");h.Rollback("nudge");Check(h.Current=="x"&&!h.CanRedo,"Rollback should drop the coalesced step");
  h.Rollback("nudge");Check(h.Current=="x","Rollback with a stale key should do nothing");
  h.MarkSaved();Check(!h.IsDirty,"MarkSaved");h.Push("z");Check(h.IsDirty,"Dirty after push");h.Undo();Check(!h.IsDirty,"Undo back to saved state should be clean");
  int changes=0;h.Changed+=()=>changes++;h.Push("q");h.Undo();Check(changes==2,"Changed event count");

  // Document operations.
  var assets=new Vibershot.Editing.AssetStore();
  var doc=Vibershot.Editing.DocumentOps.FromImage(image,assets);
  Check(doc.Size==new Size(120,100)&&doc.Layers.Count==2&&doc.Layers[0].Items[0] is Vibershot.Editing.ImageItem,"FromImage layout");
  Check(Vibershot.Editing.DocumentOps.ActiveLayerIndex(doc)==1,"Active layer should be the annotation layer");
  var rect=new Vibershot.Editing.RectItem{Bounds=new Rect(10,10,40,30),Stroke=Colors.Red,StrokeWidth=3,CornerRadius=4};
  var ellipse=new Vibershot.Editing.EllipseItem{Bounds=new Rect(20,20,10,10),Fill=Colors.Blue};
  var arrow=new Vibershot.Editing.ArrowItem{Stroke=Colors.Black,StrokeWidth=2,HeadSize=10}.WithPoints(new Point(100,80),new Point(5,5));
  var text=new Vibershot.Editing.TextItem{Bounds=new Rect(5,60,100,30),Text="Hello",FontFamily="Arial",FontSize=20,Bold=true,Italic=true,Underline=true,Color=Colors.Green,Alignment=Vibershot.Editing.TextAlign.Center,Background=Colors.Yellow,Padding=4,Rotation=15,Opacity=0.5,Shadow=true,Stroke=Colors.Gray,StrokeWidth=1};
  var doc2=Vibershot.Editing.DocumentOps.AddItems(doc,new Vibershot.Editing.Item[]{rect,ellipse,arrow,text});
  Check(doc.Layers[1].Items.Count==0&&doc2.Layers[1].Items.Count==4,"AddItems must not mutate the input");
  Check(arrow.Reversed&&arrow.Flipped==false&&arrow.Head==new Point(5,5)&&arrow.Tail==new Point(100,80),"Arrow direction");
  var moved=Vibershot.Editing.DocumentOps.MoveItems(doc2,new[]{rect.Id,ellipse.Id},new Vector(5,-5));
  Check(Vibershot.Editing.DocumentOps.FindItem(moved,rect.Id)!.Bounds==new Rect(15,5,40,30)&&Vibershot.Editing.DocumentOps.FindItem(moved,ellipse.Id)!.Bounds==new Rect(25,15,10,10)&&Vibershot.Editing.DocumentOps.FindItem(moved,text.Id)!.Bounds==text.Bounds,"MoveItems");
  var resized=Vibershot.Editing.DocumentOps.SetBounds(doc2,rect.Id,new Rect(0,0,7,8));Check(Vibershot.Editing.DocumentOps.FindItem(resized,rect.Id)!.Bounds==new Rect(0,0,7,8),"SetBounds");
  var updated=Vibershot.Editing.DocumentOps.UpdateItems(doc2,new[]{rect.Id,text.Id},i=>i with{StrokeWidth=9,Id=Guid.Empty});
  Check(Vibershot.Editing.DocumentOps.FindItem(updated,rect.Id)!.StrokeWidth==9&&Vibershot.Editing.DocumentOps.FindItem(updated,text.Id)!.StrokeWidth==9&&Vibershot.Editing.DocumentOps.FindItem(updated,ellipse.Id)!.StrokeWidth==0,"UpdateItems should keep ids and skip others");
  var removed=Vibershot.Editing.DocumentOps.RemoveItems(doc2,new[]{ellipse.Id});Check(removed.Layers[1].Items.Count==3&&Vibershot.Editing.DocumentOps.FindItem(removed,ellipse.Id)==null,"RemoveItems");
  Guid[] Order(Vibershot.Editing.ImageDocument d)=>d.Layers[1].Items.Select(i=>i.Id).ToArray();
  Check(Order(Vibershot.Editing.DocumentOps.Reorder(doc2,rect.Id,Vibershot.Editing.ZOrderMove.BringToFront)).SequenceEqual(new[]{ellipse.Id,arrow.Id,text.Id,rect.Id}),"BringToFront");
  Check(Order(Vibershot.Editing.DocumentOps.Reorder(doc2,text.Id,Vibershot.Editing.ZOrderMove.SendToBack)).SequenceEqual(new[]{text.Id,rect.Id,ellipse.Id,arrow.Id}),"SendToBack");
  Check(Order(Vibershot.Editing.DocumentOps.Reorder(doc2,rect.Id,Vibershot.Editing.ZOrderMove.BringForward)).SequenceEqual(new[]{ellipse.Id,rect.Id,arrow.Id,text.Id}),"BringForward");
  Check(Order(Vibershot.Editing.DocumentOps.Reorder(doc2,arrow.Id,Vibershot.Editing.ZOrderMove.SendBackward)).SequenceEqual(new[]{rect.Id,arrow.Id,ellipse.Id,text.Id}),"SendBackward");
  Check(ReferenceEquals(Vibershot.Editing.DocumentOps.Reorder(doc2,rect.Id,Vibershot.Editing.ZOrderMove.SendToBack),doc2),"No-op reorder should return the same document");
  var cropped=Vibershot.Editing.DocumentOps.SetCrop(doc2,new Rect(10,20,200,30));Check(cropped.CropRect==new Rect(10,20,110,30),"Crop should clamp to the canvas");
  Check(Vibershot.Editing.DocumentOps.SetCrop(doc2,new Rect(0,0,120,100)).CropRect==null&&Vibershot.Editing.DocumentOps.SetCrop(cropped,null).CropRect==null,"Full or null crop should clear");
  var rotatedBounds=Vibershot.Editing.DocumentOps.VisualBounds(new Vibershot.Editing.RectItem{Bounds=new Rect(0,0,10,10),Rotation=90});Check(Math.Abs(rotatedBounds.X)<0.001&&Math.Abs(rotatedBounds.Width-10)<0.001,"VisualBounds of a 90 degree square");
  var clones=Vibershot.Editing.DocumentOps.Clone(new[]{rect},new Vector(10,10));Check(clones[0].Id!=rect.Id&&clones[0].Bounds==new Rect(20,20,40,30),"Clone");

  // Layer operations: insert, move, remove, merge down, move items between layers, background.
  var middle=new Vibershot.Editing.Layer{Name="Middle"};var three=Vibershot.Editing.DocumentOps.InsertLayer(doc2,1,middle);
  Check(three.Layers.Count==3&&three.Layers[1].Name=="Middle"&&three.Layers[2].Items.Count==4,"InsertLayer");
  Check(Vibershot.Editing.DocumentOps.InsertLayer(doc2,99,middle).Layers[2].Name=="Middle","InsertLayer clamps to the top");
  Check(Vibershot.Editing.DocumentOps.LayerIndexOf(three,middle)==1&&Vibershot.Editing.DocumentOps.MoveLayer(three,1,1).Layers[2].Name=="Middle"&&Vibershot.Editing.DocumentOps.MoveLayer(three,1,-5).Layers[0].Name=="Middle","MoveLayer");
  Check(ReferenceEquals(Vibershot.Editing.DocumentOps.MoveLayer(three,2,1),three),"MoveLayer past the top is a no-op");
  Check(Vibershot.Editing.DocumentOps.RemoveLayer(three,1).Layers.Count==2&&Vibershot.Editing.DocumentOps.RemoveLayer(three,1).Layers[1].Items.Count==4,"RemoveLayer");
  var single=Vibershot.Editing.DocumentOps.Blank(new Size(10,10),Colors.White) with{Layers=[new Vibershot.Editing.Layer()]};Check(ReferenceEquals(Vibershot.Editing.DocumentOps.RemoveLayer(single,0),single),"The last layer cannot be removed");
  var mergedDown=Vibershot.Editing.DocumentOps.MergeLayerDown(doc2,1);Check(mergedDown.Layers.Count==1&&mergedDown.Layers[0].Items.Count==5&&mergedDown.Layers[0].Items[0] is Vibershot.Editing.ImageItem&&mergedDown.Layers[0].Items[4].Id==text.Id,"MergeLayerDown keeps order with the upper items on top");
  Check(ReferenceEquals(Vibershot.Editing.DocumentOps.MergeLayerDown(doc2,0),doc2),"MergeLayerDown on the bottom layer is a no-op");
  var movedToBase=Vibershot.Editing.DocumentOps.MoveItemsToLayer(doc2,new[]{rect.Id,text.Id},0);
  Check(movedToBase.Layers[0].Items.Count==3&&movedToBase.Layers[0].Items[1].Id==rect.Id&&movedToBase.Layers[0].Items[2].Id==text.Id&&movedToBase.Layers[1].Items.Count==2,"MoveItemsToLayer");
  Check(Vibershot.Editing.DocumentOps.SetBackground(doc2,Colors.Black).Background==Colors.Black&&ReferenceEquals(Vibershot.Editing.DocumentOps.SetBackground(doc2,doc2.Background),doc2),"SetBackground");
  Check(Vibershot.Editing.DocumentOps.FromImage(image,assets,Colors.White).Background==Colors.White&&doc.Background==Colors.Transparent,"FromImage background");

  // Pixel operations: a marquee lift copies the pixels into a floating item and clears them underneath; erase only clears.
  var liftDoc=Vibershot.Editing.DocumentOps.FromImage(image,assets,Colors.White);var liftBase=((Vibershot.Editing.ImageItem)liftDoc.Layers[0].Items[0]).AssetId;
  var lift=Vibershot.Editing.PixelOps.Lift(liftDoc,assets,new Rect(10.4,19.6,30,15));
  Check(lift.Lifted!=null&&lift.Lifted.Bounds==new Rect(10,20,30,15),"Lift snaps the region and reports the floating item");
  Check(lift.Document.Layers[0].Items.Count==2&&lift.Document.Layers[0].Items[1].Id==lift.Lifted!.Id&&lift.Document.Layers[1].Items.Count==0,"The lifted item sits on top of the layer it came from");
  var liftedBitmap=assets.Get(lift.Lifted.AssetId);Check(liftedBitmap!=null&&liftedBitmap.PixelWidth==30&&liftedBitmap.PixelHeight==15,"Lifted asset size");
  byte[] liftedPx=new byte[30*15*4];new FormatConvertedBitmap(liftedBitmap,PixelFormats.Bgra32,null,0).CopyPixels(liftedPx,120,0);byte[] srcPx=new byte[30*15*4];image.CopyPixels(new Int32Rect(10,20,30,15),srcPx,120,0);
  for(int i=0;i<liftedPx.Length;i+=4)Check(liftedPx[i]==srcPx[i]&&liftedPx[i+1]==srcPx[i+1]&&liftedPx[i+2]==srcPx[i+2]&&liftedPx[i+3]==255,"Lifted pixels should match the source at byte "+i);
  var baseAfter=(Vibershot.Editing.ImageItem)lift.Document.Layers[0].Items[0];Check(baseAfter.Id==liftDoc.Layers[0].Items[0].Id&&baseAfter.AssetId!=liftBase&&assets.Get(liftBase)!=null,"Erase swaps in a new asset and keeps the old one for undo");
  byte[] px=new byte[4];assets.Get(baseAfter.AssetId)!.CopyPixels(new Int32Rect(15,25,1,1),px,4,0);Check(px[3]==0,"Erased pixel should be transparent");
  assets.Get(baseAfter.AssetId)!.CopyPixels(new Int32Rect(39,34,1,1),px,4,0);Check(px[3]==0,"Last erased pixel should be transparent");
  assets.Get(baseAfter.AssetId)!.CopyPixels(new Int32Rect(40,35,1,1),px,4,0);Check(px[3]==255,"Pixel just outside the marquee stays");
  assets.Get(baseAfter.AssetId)!.CopyPixels(new Int32Rect(9,19,1,1),px,4,0);Check(px[3]==255,"Pixel before the marquee stays");
  Check(ReferenceEquals(Vibershot.Editing.PixelOps.Erase(liftDoc,assets,new Rect(200,200,10,10)),liftDoc)&&Vibershot.Editing.PixelOps.Lift(liftDoc,assets,new Rect(200,200,10,10)).Lifted==null,"Regions off the canvas are no-ops");
  var lockedDoc=Vibershot.Editing.DocumentOps.SetLayer(liftDoc,0,liftDoc.Layers[0] with{Locked=true});
  Check(Vibershot.Editing.PixelOps.Lift(lockedDoc,assets,new Rect(0,0,10,10)).Lifted==null&&ReferenceEquals(Vibershot.Editing.PixelOps.Erase(lockedDoc,assets,new Rect(0,0,10,10)),lockedDoc),"Locked layers are left alone");
  var rotatedDoc=Vibershot.Editing.DocumentOps.ReplaceItem(liftDoc,liftDoc.Layers[0].Items[0] with{Rotation=10});Check(Vibershot.Editing.PixelOps.Lift(rotatedDoc,assets,new Rect(0,0,10,10)).Lifted==null,"Rotated images are left alone");
  var scaled=Vibershot.Editing.DocumentOps.AddItem(Vibershot.Editing.DocumentOps.Blank(new Size(60,50),Colors.Transparent),new Vibershot.Editing.ImageItem{AssetId=liftBase,Bounds=new Rect(0,0,60,50)},0);
  var scaledErased=(Vibershot.Editing.ImageItem)Vibershot.Editing.PixelOps.Erase(scaled,assets,new Rect(0,0,30,25)).Layers[0].Items[0];
  assets.Get(scaledErased.AssetId)!.CopyPixels(new Int32Rect(59,49,1,1),px,4,0);Check(px[3]==0,"Erase maps document space to a scaled image's pixels");
  assets.Get(scaledErased.AssetId)!.CopyPixels(new Int32Rect(60,0,1,1),px,4,0);Check(px[3]==255,"Erase on a scaled image stops at the mapped edge");
  var cleared=Vibershot.Editing.PixelOps.ClearRect(image,new Int32Rect(-5,-5,10,10));cleared.CopyPixels(new Int32Rect(4,4,1,1),px,4,0);Check(px[3]==0&&cleared.IsFrozen,"ClearRect clamps and freezes");

  // Editor settings: the canvas colour parses as hex or the word transparent and defaults to white.
  var editorDefaults=new EditorSettings();Check(editorDefaults.CanvasBackground==EditorSettings.White&&editorDefaults.CanvasBackgroundArgb()==0xFFFFFFFF,"Canvas background defaults to white");
  Check(EditorSettings.ParseArgb("transparent")==0u&&EditorSettings.ParseArgb("#00000000")==0u&&EditorSettings.ParseArgb("#80FF0000")==0x80FF0000u&&EditorSettings.ParseArgb("000000")==0xFF000000u&&EditorSettings.ParseArgb("nope")==null&&EditorSettings.ParseArgb("")==null,"Canvas background parsing");
  Check(new EditorSettings{CanvasBackground="bad"}.CanvasBackgroundArgb()==0xFFFFFFFF,"Unreadable canvas colour falls back to white");
  Check(JsonSerializer.Deserialize<AppConfig>("{\"Editor\":{\"DefaultFontSize\":20}}")!.Editor.CanvasBackground==EditorSettings.White,"Older configs get the white canvas");

  // JSON polymorphism: every item kind round trips with its discriminator and properties.
  var itemsJson=Vibershot.Editing.ItemClipboard.Serialize(new Vibershot.Editing.Item[]{rect,ellipse,arrow,text,doc.Layers[0].Items[0]});
  Check(itemsJson.Contains("\"type\": \"rect\"")&&itemsJson.Contains("\"type\": \"arrow\"")&&itemsJson.Contains("\"type\": \"text\"")&&itemsJson.Contains("\"type\": \"image\"")&&itemsJson.Contains("\"type\": \"ellipse\""),"Discriminators missing");
  var back=Vibershot.Editing.ItemClipboard.Deserialize(itemsJson);Check(back.Count==5&&back[0] is Vibershot.Editing.RectItem&&back[1] is Vibershot.Editing.EllipseItem&&back[2] is Vibershot.Editing.ArrowItem&&back[3] is Vibershot.Editing.TextItem&&back[4] is Vibershot.Editing.ImageItem,"Polymorphic items");
  Check(back[0].Equals(rect)&&back[1].Equals(ellipse)&&back[2].Equals(arrow)&&back[3].Equals(text),"Item properties changed in JSON round trip");
  Check(Vibershot.Editing.DocumentJson.ParseColor("#F0612A")==Color.FromRgb(0xF0,0x61,0x2A)&&Vibershot.Editing.DocumentJson.ColorToString(Colors.Red)=="#FFFF0000","Color parsing");

  // Project file round trip keeps the document, every item property and the asset pixels.
  var project=Vibershot.Editing.DocumentOps.SetCrop(doc2,new Rect(10,10,60,50)) with{Background=Colors.White};
  project=Vibershot.Editing.DocumentOps.SetLayer(project,1,project.Layers[1] with{Name="Notes",Opacity=0.75,Locked=true,Visible=false});
  var projectPath=Path.Combine(AppContext.BaseDirectory,"roundtrip.vibeshot");Vibershot.Editing.ProjectFile.Save(projectPath,project,assets);
  var (loaded,loadedAssets)=Vibershot.Editing.ProjectFile.Load(projectPath);
  Check(loaded.Size==project.Size&&loaded.CropRect==project.CropRect&&loaded.Background==project.Background&&loaded.Layers.Count==2&&loaded.Version==Vibershot.Editing.ImageDocument.CurrentVersion,"Project document fields");
  Check(Vibershot.Editing.DocumentJson.Serialize(project).Contains("\"version\": 1")||Vibershot.Editing.DocumentJson.Serialize(project).Contains("\"Version\": 1"),"Project JSON carries a schema version");
  Check(Vibershot.Editing.DocumentJson.Deserialize<Vibershot.Editing.ImageDocument>("{\"Size\":[10,10],\"Layers\":[]}")!.Version==1,"Documents without a version read as version 1");
  Check(loaded.Layers[1].Name=="Notes"&&loaded.Layers[1].Opacity==0.75&&loaded.Layers[1].Locked&&!loaded.Layers[1].Visible&&loaded.Layers[1].Id==project.Layers[1].Id,"Project layer fields");
  var originalItems=project.AllItems.ToList();var loadedItems=loaded.AllItems.ToList();Check(loadedItems.Count==originalItems.Count,"Project item count");
  for(int i=0;i<originalItems.Count;i++)Check(loadedItems[i].Equals(originalItems[i]),"Project item "+i+" changed: "+originalItems[i].GetType().Name);
  var baseAsset=((Vibershot.Editing.ImageItem)loaded.Layers[0].Items[0]).AssetId;var loadedBitmap=loadedAssets.Get(baseAsset);Check(loadedBitmap!=null&&loadedBitmap.PixelWidth==120&&loadedBitmap.PixelHeight==100,"Project asset missing");
  byte[] original=new byte[120*100*4];image.CopyPixels(original,480,0);byte[] restored=new byte[120*100*4];new FormatConvertedBitmap(loadedBitmap,PixelFormats.Bgra32,null,0).CopyPixels(restored,480,0);Check(original.AsSpan().SequenceEqual(restored),"Project asset pixels changed");
  // The project carries a flattened preview the gallery can draw without loading the document.
  var preview=Vibershot.Editing.ProjectFile.ReadPreview(projectPath);Check(preview!=null&&preview.PixelWidth==60&&preview.PixelHeight==50&&preview.IsFrozen,"Project preview matches the crop size");
  Check(Vibershot.Editing.ProjectFile.ReadSize(projectPath) is (60,50),"Project size read from document.json");
  var galleryPreview=ThumbnailCache.LoadProjectPreview(projectPath,30);Check(galleryPreview!=null&&galleryPreview.PixelWidth==30&&galleryPreview.PixelHeight==25,"Gallery scales the project preview");
  var wideDoc=Vibershot.Editing.DocumentOps.FromImage(image,assets) with{Size=new Size(2000,500)};Check(Vibershot.Editing.ProjectFile.RenderPreview(wideDoc,assets).PixelWidth==Vibershot.Editing.ProjectFile.PreviewEdge,"Preview is capped at the preview edge");
  using(var zip=System.IO.Compression.ZipFile.OpenRead(projectPath))Check(zip.GetEntry("preview.png")!=null&&zip.GetEntry("document.json")!=null,"Project zip entries");
  var noPreview=Path.Combine(AppContext.BaseDirectory,"nopreview.vibeshot");File.Delete(noPreview);using(var zip=System.IO.Compression.ZipFile.Open(noPreview,System.IO.Compression.ZipArchiveMode.Create)){using var e=new StreamWriter(zip.CreateEntry("document.json").Open());e.Write(Vibershot.Editing.DocumentJson.Serialize(project));}
  Check(Vibershot.Editing.ProjectFile.ReadPreview(noPreview)==null&&ThumbnailCache.LoadProjectPreview(noPreview,30)==null,"Projects without a preview return null");

  // Flatten honours the crop and renders items; a tall document renders in bands.
  var flat=Vibershot.Editing.DocumentRenderer.Flatten(Vibershot.Editing.DocumentOps.SetCrop(doc,new Rect(10,20,50,40)),assets);
  Check(flat.PixelWidth==50&&flat.PixelHeight==40,"Flatten should use the crop size");
  byte[] flatPixels=new byte[50*40*4];flat.CopyPixels(flatPixels,200,0);byte[] srcRow=new byte[480];image.CopyPixels(new Int32Rect(0,20,120,1),srcRow,480,0);
  for(int x=0;x<50;x++)for(int c=0;c<3;c++)Check(flatPixels[x*4+c]==srcRow[(x+10)*4+c],"Flatten pixel mismatch at x="+x);
  var full=Vibershot.Editing.DocumentRenderer.Flatten(doc,assets);Check(full.PixelWidth==120&&full.PixelHeight==100,"Flatten without crop should be the canvas size");
  var withFill=Vibershot.Editing.DocumentOps.AddItem(Vibershot.Editing.DocumentOps.Blank(new Size(20,20),Colors.Transparent),new Vibershot.Editing.RectItem{Bounds=new Rect(0,0,20,20),Fill=Colors.Red});
  var fillFlat=Vibershot.Editing.DocumentRenderer.Flatten(withFill,assets);byte[] fp=new byte[20*20*4];fillFlat.CopyPixels(fp,80,0);Check(fp[2]==255&&fp[1]==0&&fp[0]==0&&fp[3]==255,"Filled rect should render red");
  var tall=Vibershot.Editing.DocumentOps.AddItem(Vibershot.Editing.DocumentOps.Blank(new Size(4,Vibershot.Editing.DocumentRenderer.BandHeight+10),Colors.Transparent),new Vibershot.Editing.RectItem{Bounds=new Rect(0,Vibershot.Editing.DocumentRenderer.BandHeight+5,4,5),Fill=Colors.Blue});
  var tallFlat=Vibershot.Editing.DocumentRenderer.Flatten(tall,assets);Check(tallFlat.PixelHeight==Vibershot.Editing.DocumentRenderer.BandHeight+10,"Banded flatten height");
  byte[] lastRow=new byte[16];tallFlat.CopyPixels(new Int32Rect(0,tallFlat.PixelHeight-1,4,1),lastRow,16,0);Check(lastRow[0]==255&&lastRow[2]==0&&lastRow[3]==255,"Banded flatten should render the last band");
  byte[] firstRow=new byte[16];tallFlat.CopyPixels(new Int32Rect(0,0,4,1),firstRow,16,0);Check(firstRow[3]==0,"Banded flatten first row should be transparent");
  var itemsFlat=Vibershot.Editing.DocumentRenderer.FlattenItems(new[]{rect},assets);Check(itemsFlat.PixelWidth==48&&itemsFlat.PixelHeight==38,"FlattenItems size should be the item bounds plus margin");
  var measured=Vibershot.Editing.DocumentRenderer.MeasureText(text);Check(measured.Width>8&&measured.Height>20,"MeasureText");

  // Editor depth: callouts, stamps, arrow heads, canvas resize and scale, blur patches.
  {
   var E=typeof(Vibershot.Editing.DocumentOps);
   var callout=new Vibershot.Editing.CalloutItem{Bounds=new Rect(100,100,200,60),Text="Label",Kind=Vibershot.Editing.CalloutKind.Speech,Background=Colors.White,Stroke=Colors.Red,StrokeWidth=3,Targets=[new Point(40,260),new Point(420,90)]};
   var stamp=new Vibershot.Editing.StampItem{Bounds=new Rect(10,10,48,48),StampId="smiley",Fill=Colors.Lime};
   var roundArrow=(Vibershot.Editing.ArrowItem)new Vibershot.Editing.ArrowItem{Stroke=Colors.Black,StrokeWidth=4,HeadSize=20,HeadRoundness=0.8}.WithPoints(new Point(10,10),new Point(90,60));
   var json2=Vibershot.Editing.ItemClipboard.Serialize(new Vibershot.Editing.Item[]{callout,stamp,roundArrow});
   var back2=Vibershot.Editing.ItemClipboard.Deserialize(json2);
   Check(back2.Count==3&&back2[0] is Vibershot.Editing.CalloutItem bc&&bc.Kind==Vibershot.Editing.CalloutKind.Speech&&bc.Targets.Count==2&&bc.Targets[1]==new Point(420,90)&&bc.Text=="Label","Callout round trip: "+json2);
   Check(back2[1] is Vibershot.Editing.StampItem bs&&bs.StampId=="smiley"&&bs.Fill==Colors.Lime&&back2[2] is Vibershot.Editing.ArrowItem ba&&ba.HeadRoundness==0.8,"Stamp and arrow roundness round trip");
   Check(json2.Contains("\"type\": \"callout\"")&&json2.Contains("\"type\": \"stamp\""),"Callout and stamp discriminators");
   var plainText=Vibershot.Editing.ItemClipboard.Deserialize(Vibershot.Editing.ItemClipboard.Serialize(new Vibershot.Editing.Item[]{new Vibershot.Editing.TextItem{Text="t"}}));
   Check(plainText[0].GetType()==typeof(Vibershot.Editing.TextItem),"A text item must not come back as a callout");
   Check(Vibershot.Editing.ItemClipboard.Deserialize("[{\"type\":\"arrow\",\"Bounds\":[0,0,10,10]}]")[0] is Vibershot.Editing.ArrowItem old&&old.HeadRoundness==0.25,"Arrows saved before roundness existed load with the default");

   var cdoc=Vibershot.Editing.DocumentOps.AddItems(Vibershot.Editing.DocumentOps.Blank(new Size(500,300),Colors.White),new Vibershot.Editing.Item[]{callout,stamp});
   var movedAll=(Vibershot.Editing.CalloutItem)Vibershot.Editing.DocumentOps.FindItem(Vibershot.Editing.DocumentOps.MoveItems(cdoc,new[]{callout.Id},new Vector(10,5)),callout.Id)!;
   Check(movedAll.Bounds==new Rect(110,105,200,60)&&movedAll.Targets[0]==new Point(50,265),"Moving a callout takes its pointers along");
   var pinned=(Vibershot.Editing.CalloutItem)Vibershot.Editing.DocumentOps.FindItem(Vibershot.Editing.DocumentOps.MoveItems(cdoc,new[]{callout.Id},new Vector(10,5),pinTargets:true),callout.Id)!;
   Check(pinned.Bounds==new Rect(110,105,200,60)&&pinned.Targets[0]==new Point(40,260),"Pinned move leaves the pointers");
   var vb=Vibershot.Editing.DocumentOps.VisualBounds(callout);Check(vb.Left==40&&vb.Bottom==260&&vb.Right==420&&vb.Top==90,"Callout visual bounds include its targets: "+vb);
   var clone=(Vibershot.Editing.CalloutItem)Vibershot.Editing.DocumentOps.Clone(new Vibershot.Editing.Item[]{callout},new Vector(10,10))[0];
   Check(clone.Id!=callout.Id&&clone.Targets[0]==new Point(50,270),"Clone offsets pointers");

   var cropDoc=Vibershot.Editing.DocumentOps.SetCrop(cdoc,new Rect(50,40,300,200));
   var grown=Vibershot.Editing.DocumentOps.ResizeCanvas(cropDoc,20,10,30,40);
   var gc=(Vibershot.Editing.CalloutItem)Vibershot.Editing.DocumentOps.FindItem(grown,callout.Id)!;
   Check(grown.Size==new Size(550,350)&&gc.Bounds==new Rect(120,110,200,60)&&gc.Targets[1]==new Point(440,100)&&grown.CropRect==new Rect(70,50,300,200)&&cropDoc.Size==new Size(500,300),"ResizeCanvas shifts items, pointers and crop");
   var trimmed=Vibershot.Editing.DocumentOps.ResizeCanvas(cdoc,-100,0,0,-50);
   Check(trimmed.Size==new Size(400,250)&&Vibershot.Editing.DocumentOps.FindItem(trimmed,stamp.Id)!.Bounds==new Rect(-90,10,48,48),"Trimming keeps items, shifted");
   Check(ReferenceEquals(Vibershot.Editing.DocumentOps.ResizeCanvas(cdoc,0,0,0,0),cdoc),"ResizeCanvas with no change returns the input");
   var toCrop=Vibershot.Editing.DocumentOps.TrimToCrop(cropDoc);
   Check(toCrop.Size==new Size(300,200)&&toCrop.CropRect==null&&Vibershot.Editing.DocumentOps.FindItem(toCrop,callout.Id)!.Bounds==new Rect(50,60,200,60),"TrimToCrop makes the crop the canvas");
   var doubled=Vibershot.Editing.DocumentOps.Scale(Vibershot.Editing.DocumentOps.AddItem(cropDoc,roundArrow),new Size(1000,600));
   var dc2=(Vibershot.Editing.CalloutItem)Vibershot.Editing.DocumentOps.FindItem(doubled,callout.Id)!;var da=(Vibershot.Editing.ArrowItem)Vibershot.Editing.DocumentOps.FindItem(doubled,roundArrow.Id)!;
   Check(doubled.Size==new Size(1000,600)&&dc2.Bounds==new Rect(200,200,400,120)&&dc2.FontSize==36&&dc2.StrokeWidth==6&&dc2.Targets[0]==new Point(80,520)&&da.HeadSize==40&&da.StrokeWidth==8&&da.Head==new Point(180,120)&&doubled.CropRect==new Rect(100,80,600,400),"Scale doubles geometry, strokes, fonts and crop");
   Check(Vibershot.Editing.DocumentOps.IsValidCanvas(3840,2160)&&!Vibershot.Editing.DocumentOps.IsValidCanvas(0,10)&&!Vibershot.Editing.DocumentOps.IsValidCanvas(40000,10)&&!Vibershot.Editing.DocumentOps.IsValidCanvas(20000,20000),"Canvas limits");

   // Rendering: every callout kind draws a body and reaches towards its target; stamps and round arrow heads draw.
   byte[] Px(BitmapSource b,int x,int y){var px=new byte[4];b.CopyPixels(new Int32Rect(x,y,1,1),px,4,0);return px;}
   foreach(var kind in Enum.GetValues<Vibershot.Editing.CalloutKind>())
   {
    var c=callout with{Kind=kind,Targets=[new Point(40,260)]};
    var flat2=Vibershot.Editing.DocumentRenderer.Flatten(new Vibershot.Editing.ImageDocument{Size=new Size(500,300),Layers=[new Vibershot.Editing.Layer{Items=[c]}]},assets);
    Check(Px(flat2,200,130)[3]==255,kind+" callout body is filled");
    // Somewhere along the line from the box to the target there is ink (arrow, tail or thought bubbles).
    bool ink=false;for(double t=0.55;t<0.98;t+=0.01){int x=(int)(200+(40-200)*t),y=(int)(130+(260-130)*t);if(Px(flat2,x,y)[3]>0){ink=true;break;}}
    Check(ink,kind+" callout points at its target");
    Check(Px(flat2,480,280)[3]==0,kind+" callout leaves the far corner clear");
    var size=Vibershot.Editing.DocumentRenderer.MeasureText(c);var area=Vibershot.Editing.DocumentRenderer.TextArea(c);
    Check(size.Width>10&&area.Width>0&&area.Width<=c.Bounds.Width&&c.Bounds.Contains(area),kind+" text area sits inside the body");
   }
   var rotatedCallout=callout with{Rotation=30,Kind=Vibershot.Editing.CalloutKind.Label};
   Vibershot.Editing.DocumentRenderer.Flatten(new Vibershot.Editing.ImageDocument{Size=new Size(500,300),Layers=[new Vibershot.Editing.Layer{Items=[rotatedCallout,callout with{Shadow=true,Kind=Vibershot.Editing.CalloutKind.Thought}]}]},assets);
   Check(Vibershot.Editing.StampLibrary.All.Count>=10&&Vibershot.Editing.StampLibrary.All.Select(d=>d.Id).Distinct().Count()==Vibershot.Editing.StampLibrary.All.Count,"Stamp ids are unique");
   Check(Vibershot.Editing.StampLibrary.Search("mouse").Any(d=>d.Id=="pointer")&&Vibershot.Editing.StampLibrary.Search("home").Single().Id=="house"&&Vibershot.Editing.StampLibrary.Search("smile").Any(d=>d.Id=="smiley")&&!Vibershot.Editing.StampLibrary.Search("zzzz").Any()&&Vibershot.Editing.StampLibrary.Search("").Count()==Vibershot.Editing.StampLibrary.All.Count,"Stamp search");
   foreach(var def in Vibershot.Editing.StampLibrary.All)
   {
    var sflat=Vibershot.Editing.DocumentRenderer.Flatten(new Vibershot.Editing.ImageDocument{Size=new Size(96,96),Layers=[new Vibershot.Editing.Layer{Items=[new Vibershot.Editing.StampItem{StampId=def.Id,Bounds=new Rect(8,8,80,80),Shadow=true}]}]},assets);
    var sp=new byte[96*96*4];sflat.CopyPixels(sp,96*4,0);int opaque=0;for(int i=3;i<sp.Length;i+=4)if(sp[i]>0)opaque++;
    Check(opaque>150,"Stamp '"+def.Id+"' draws something");
   }
   var libThemes=Vibershot.Editing.StyleLibrary.Themes(Colors.Red,3);
   Check(libThemes.Count>=5&&libThemes[0].Id==Vibershot.Editing.StyleLibrary.DefaultThemeId&&libThemes[0].Primary==Colors.Red&&libThemes[0].StrokeWidth==3&&libThemes.Select(t=>t.Id).Distinct().Count()==libThemes.Count,"Style themes: Settings defaults first, ids unique");
   foreach(var theme in libThemes)
   {
    var defaults=Vibershot.Editing.StyleLibrary.Defaults(theme);
    Check(defaults.Arrow.Stroke==theme.Primary&&defaults.Arrow.StrokeWidth==theme.StrokeWidth&&defaults.Line.Stroke==theme.Primary&&defaults.Rect.CornerRadius==theme.CornerRadius&&defaults.Ellipse.Stroke==theme.Primary&&defaults.Text.Color==theme.Primary&&defaults.Callout.Stroke==theme.Primary&&defaults.Callout.Background==Colors.White,"Theme '"+theme.Id+"' sets the tool styles");
    Check(Vibershot.Editing.StyleLibrary.Find(theme,Vibershot.Editing.PresetGroup.Shapes,"tinted")?.Style.Fill==theme.Tint&&Vibershot.Editing.StyleLibrary.Find(theme,Vibershot.Editing.PresetGroup.Shapes,"nope")==null,"Preset lookup in theme '"+theme.Id+"'");
    foreach(var group in Enum.GetValues<Vibershot.Editing.PresetGroup>())
    {
     var presets=Vibershot.Editing.StyleLibrary.Presets(theme,group);
     Check(presets.Count>=6&&presets.Select(p=>p.Id).Distinct().Count()==presets.Count&&presets.All(p=>p.Group==group),"Presets of "+theme.Id+"/"+group+" have unique ids");
     foreach(var preset in presets)
     {
      string where=theme.Id+"/"+group+"/"+preset.Id;
      Check(new Rect(Vibershot.Editing.StyleLibrary.SampleSize).Contains(preset.Sample.Bounds)&&preset.Sample.GetType()==preset.Style.GetType(),"Sample of "+where+" fits its tile");
      var pflat=Vibershot.Editing.DocumentRenderer.Flatten(new Vibershot.Editing.ImageDocument{Size=Vibershot.Editing.StyleLibrary.SampleSize,Layers=[new Vibershot.Editing.Layer{Items=[preset.Sample]}]},assets);
      var pp=new byte[96*64*4];pflat.CopyPixels(pp,96*4,0);int drawn=0;for(int i=3;i<pp.Length;i+=4)if(pp[i]>0)drawn++;
      Check(drawn>60,"Sample of "+where+" draws something");
      var placed=Vibershot.Editing.StyleLibrary.Place(preset.Style,preset.Sample,new Point(400.4,300.2),2.5);
      Check(placed.Id!=preset.Style.Id&&placed.Id!=preset.Sample.Id&&placed.GetType()==preset.Style.GetType()&&placed.Stroke==preset.Style.Stroke&&placed.StrokeWidth==preset.Style.StrokeWidth&&placed.Fill==preset.Style.Fill,"Placing "+where+" keeps its style");
      if(placed is Vibershot.Editing.CalloutItem pc)Check(pc.Text==""&&pc.Targets.Count==1&&pc.Bounds.Width>=200&&pc.Bounds.Height>=56&&!pc.Bounds.Contains(pc.Targets[0])&&pc.Kind==((Vibershot.Editing.CalloutItem)preset.Style).Kind,"Placed callout "+where+" points away from its box");
      else Check(Math.Abs(placed.Center.X-400)<=1&&Math.Abs(placed.Center.Y-300)<=1&&Math.Abs(placed.Bounds.Width-Math.Round(preset.Sample.Bounds.Width*2.5))<0.01,"Placed "+where+" is centred and scaled: "+placed.Bounds);
      if(placed is Vibershot.Editing.LineItem pl&&preset.Sample is Vibershot.Editing.LineItem sl)Check(pl.Flipped==sl.Flipped&&pl.Reversed==sl.Reversed,"Placed line "+where+" keeps its direction");
     }
    }
   }
   var themedConfig=AppConfig.FromJson("{\"Editor\":{\"Theme\":\"marker\",\"SidebarCollapsed\":true}}")!;Check(themedConfig.Editor.Theme=="marker"&&themedConfig.Editor.SidebarCollapsed&&new AppConfig().Editor.Theme==Vibershot.Editing.StyleLibrary.DefaultThemeId&&!new AppConfig().Editor.SidebarCollapsed,"Editor theme and sidebar settings round trip");
   var sharp=Vibershot.Editing.DocumentRenderer.Flatten(new Vibershot.Editing.ImageDocument{Size=new Size(100,70),Layers=[new Vibershot.Editing.Layer{Items=[roundArrow with{HeadRoundness=0}]}]},assets);
   var blunt=Vibershot.Editing.DocumentRenderer.Flatten(new Vibershot.Editing.ImageDocument{Size=new Size(100,70),Layers=[new Vibershot.Editing.Layer{Items=[roundArrow with{HeadRoundness=1}]}]},assets);
   int Ink(BitmapSource b){var px=new byte[b.PixelWidth*b.PixelHeight*4];b.CopyPixels(px,b.PixelWidth*4,0);int n=0;for(int i=3;i<px.Length;i+=4)if(px[i]>127)n++;return n;}
   Check(Px(sharp,88,58)[3]>0&&Px(blunt,88,58)[3]>0&&Ink(blunt)<Ink(sharp)*0.9,"Sharp and rounded heads both touch the tip; the rounded one is a smaller shape");

   // Blur and pixelate patches.
   var flatBefore=Vibershot.Editing.DocumentRenderer.Flatten(doc,assets);
   var patch=Vibershot.Editing.PixelOps.ObscurePatch(doc,assets,new Rect(10,10,40,30),Vibershot.Editing.ObscureMode.Blur,6,0)!;
   var patchBitmap=assets.Get(patch.AssetId)!;
   Check(patch.Bounds==new Rect(10,10,40,30)&&patchBitmap.PixelWidth==40&&patchBitmap.PixelHeight==30,"Blur patch covers the region");
   var before=new byte[40*30*4];flatBefore.CopyPixels(new Int32Rect(10,10,40,30),before,160,0);var after=new byte[40*30*4];patchBitmap.CopyPixels(after,160,0);
   long diff=0;for(int i=0;i<before.Length;i++)diff+=Math.Abs(before[i]-after[i]);Check(diff>before.Length*5&&after[3]==255,"Blur changes the pixels and keeps them opaque");
   long Rough(byte[] px){long r=0;for(int y=0;y<30;y++)for(int x=1;x<40;x++)r+=Math.Abs(px[(y*40+x)*4]-px[(y*40+x-1)*4]);return r;}
   Check(Rough(after)*4<Rough(before),"Blurred pixels are much smoother than the source");
   var blocks=Vibershot.Editing.PixelOps.ObscurePatch(doc,assets,new Rect(10,10,40,30),Vibershot.Editing.ObscureMode.Pixelate,10,0)!;
   var bp=new byte[40*30*4];assets.Get(blocks.AssetId)!.CopyPixels(bp,160,0);
   Check(bp[0]==bp[(9*40+9)*4]&&bp[1]==bp[(9*40+9)*4+1]&&bp[(15*40+25)*4]==bp[(11*40+21)*4],"Pixelate fills each block with one colour");
   var masked=Vibershot.Editing.PixelOps.ObscurePatch(doc,assets,new Rect(10,10,40,30),Vibershot.Editing.ObscureMode.Blur,6,0,new EllipseGeometry(new Point(30,25),10,10))!;
   var mp=new byte[40*30*4];assets.Get(masked.AssetId)!.CopyPixels(mp,160,0);
   Check(mp[3]==0&&mp[(15*40+20)*4+3]==255,"A masked patch is transparent outside the mask");
   Check(Vibershot.Editing.PixelOps.ObscurePatch(doc,assets,new Rect(500,500,10,10),Vibershot.Editing.ObscureMode.Blur,6,0)==null,"A region off the canvas makes no patch");
   var flatGrey=new byte[20*20*4];Array.Fill(flatGrey,(byte)120);Vibershot.Editing.PixelOps.BoxBlur(flatGrey,20,20,5,4);Check(flatGrey.All(v=>v==120),"Blurring a flat colour leaves it unchanged");
   var topOnly=Vibershot.Editing.DocumentOps.AddItem(doc,new Vibershot.Editing.RectItem{Bounds=new Rect(0,0,120,100),Fill=Colors.Black});
   var under=new byte[4];assets.Get(Vibershot.Editing.PixelOps.ObscurePatch(topOnly,assets,new Rect(10,10,40,30),Vibershot.Editing.ObscureMode.Pixelate,40,0)!.AssetId)!.CopyPixels(new Int32Rect(5,5,1,1),under,4,0);
   var over=new byte[4];assets.Get(Vibershot.Editing.PixelOps.ObscurePatch(topOnly,assets,new Rect(10,10,40,30),Vibershot.Editing.ObscureMode.Pixelate,40,1)!.AssetId)!.CopyPixels(new Int32Rect(5,5,1,1),over,4,0);
   Check(under[0]+under[1]+under[2]>0&&over[0]+over[1]+over[2]==0,"A patch only sees its own layer and the ones below");

   // Settings added with these features.
   var es=new EditorSettings();Check(es.ActualSizeShortcut=="Ctrl+0"&&es.FitShortcut=="","Ctrl+0 is actual size and fit has no shortcut by default");
   Check(new AppConfig().CustomRegionPixels()==(1280,720)&&new AppConfig{CustomRegionSize="1600 x 900"}.CustomRegionPixels()==(1600,900)&&new AppConfig{CustomRegionSize="junk"}.CustomRegionPixels()==(1280,720),"Custom region size parsing");
   var gs=new GallerySettings();Check(gs.RailWidth==200&&gs.DetailWidth==330,"Gallery pane width defaults");
  }
 }
}
