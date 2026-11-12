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
 }
 static void GalleryChecks(BitmapSource image) {
  // Legacy config without a Gallery section loads with defaults; a saved section round-trips.
  var legacy=JsonSerializer.Deserialize<AppConfig>("{\"Autosave\":true}")!;
  if(legacy.Gallery is null||legacy.Gallery.ThumbnailSize!=200||legacy.Gallery.OpenGalleryAfterCapture||!legacy.Gallery.ShowDetailPane||legacy.Gallery.WindowWidth is not null)throw new Exception("Gallery config defaults");
  if(!legacy.Gallery.LibraryRoot.EndsWith("Vibershot",StringComparison.OrdinalIgnoreCase))throw new Exception("Gallery library root default");
  var withGallery=JsonSerializer.Deserialize<AppConfig>("{\"Gallery\":{\"ThumbnailSize\":260,\"OpenGalleryAfterCapture\":true,\"ExtraFolders\":[\"C:\\\\Shots\"],\"WindowWidth\":1000.5}}")!;
  if(withGallery.Gallery.ThumbnailSize!=260||!withGallery.Gallery.OpenGalleryAfterCapture||withGallery.Gallery.ExtraFolders.Count!=1||withGallery.Gallery.WindowWidth!=1000.5)throw new Exception("Gallery config not read");
  var roundTrip=JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(withGallery))!;if(roundTrip.Gallery.ThumbnailSize!=260||roundTrip.Gallery.ExtraFolders[0]!="C:\\Shots")throw new Exception("Gallery config round trip");

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
  Directory.Delete(dir,true);

 }
}
static class EditorChecks {
 static void Check(bool ok,string what){if(!ok)throw new Exception(what);}
 public static void Run(BitmapSource image){
  // Legacy config without an Editor section gets the defaults; a saved section reads back.
  var legacy=JsonSerializer.Deserialize<AppConfig>("{\"Autosave\":true}")!;Check(legacy.Editor.DefaultFontFamily=="Segoe UI"&&legacy.Editor.DefaultFontSize==18&&legacy.Editor.DefaultStrokeColor=="#F0612A"&&legacy.Editor.DefaultStrokeWidth==3&&!legacy.Editor.OpenEditorAfterCapture,"Editor settings defaults");
  var editorCfg=JsonSerializer.Deserialize<AppConfig>("{\"Editor\":{\"OpenEditorAfterCapture\":true,\"DefaultStrokeWidth\":5}}")!;Check(editorCfg.Editor.OpenEditorAfterCapture&&editorCfg.Editor.DefaultStrokeWidth==5,"Editor settings not read");
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
  Check(loaded.Size==project.Size&&loaded.CropRect==project.CropRect&&loaded.Background==project.Background&&loaded.Layers.Count==2,"Project document fields");
  Check(loaded.Layers[1].Name=="Notes"&&loaded.Layers[1].Opacity==0.75&&loaded.Layers[1].Locked&&!loaded.Layers[1].Visible&&loaded.Layers[1].Id==project.Layers[1].Id,"Project layer fields");
  var originalItems=project.AllItems.ToList();var loadedItems=loaded.AllItems.ToList();Check(loadedItems.Count==originalItems.Count,"Project item count");
  for(int i=0;i<originalItems.Count;i++)Check(loadedItems[i].Equals(originalItems[i]),"Project item "+i+" changed: "+originalItems[i].GetType().Name);
  var baseAsset=((Vibershot.Editing.ImageItem)loaded.Layers[0].Items[0]).AssetId;var loadedBitmap=loadedAssets.Get(baseAsset);Check(loadedBitmap!=null&&loadedBitmap.PixelWidth==120&&loadedBitmap.PixelHeight==100,"Project asset missing");
  byte[] original=new byte[120*100*4];image.CopyPixels(original,480,0);byte[] restored=new byte[120*100*4];new FormatConvertedBitmap(loadedBitmap,PixelFormats.Bgra32,null,0).CopyPixels(restored,480,0);Check(original.AsSpan().SequenceEqual(restored),"Project asset pixels changed");

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
 }
}
