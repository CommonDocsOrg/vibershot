using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vibershot.Core;
using Vibershot.Gallery;
class Program {
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
  GalleryChecks(first);
  Console.WriteLine("PASS: overlap and pixels, duplicate rejection, AVI frame counts/chunk sizes, legacy settings defaults, WebP lossy/lossless encoding, gallery sidecar/index/query/thumbnail key/config.");
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
