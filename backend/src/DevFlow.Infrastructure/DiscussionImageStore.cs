using DevFlow.Core;

namespace DevFlow.Infrastructure;

// Only generated IDs become paths; original filenames are display-only metadata.
public sealed class DiscussionImageStore(string artifactsRoot)
{
    public const int MaxBytes=4*1024*1024, MaxPerMessage=4, MaxPerDiscussion=20, MaxDiscussionBytes=20*1024*1024;
    private string PathFor(Guid discussion,Guid image)=>Path.Combine(Path.GetFullPath(artifactsRoot),"discussion-images",discussion.ToString("N"),image.ToString("N"));
    public static byte[] Decode(ImageUpload input)
    {
        if(input is null)throw new ArgumentException("圖片附件不能為空。");
        if(input.Data is null||input.Data.Length>4*((MaxBytes+2)/3))throw new ArgumentException("每張圖片上限 4 MB。");
        byte[] bytes;
        try{bytes=Convert.FromBase64String(input.Data);}catch(FormatException){throw new ArgumentException("圖片資料不是有效的 Base64。");}
        var valid=input.MimeType switch{
            "image/png"=>bytes.Length>=24&&bytes.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}),
            "image/jpeg"=>bytes.Length>=4&&bytes[0]==255&&bytes[1]==216&&bytes[2]==255&&bytes[^2]==255&&bytes[^1]==217,
            "image/webp"=>bytes.Length>=16&&bytes.AsSpan(0,4).SequenceEqual("RIFF"u8)&&bytes.AsSpan(8,4).SequenceEqual("WEBP"u8),
            _=>false};
        if(bytes.Length==0||bytes.Length>MaxBytes||!valid)throw new ArgumentException("請使用有效的 PNG、JPEG 或 WebP 圖片，每張上限 4 MB。");
        return bytes;
    }
    public async Task<DiscussionImage[]> SaveAsync(Guid discussion,ImageUpload[] uploads,CancellationToken ct,int existingBytes=0)
    {
        if(uploads.Length>MaxPerMessage)throw new ArgumentException("每則訊息最多附上 4 張圖片。");
        // Validate the entire batch before writing any files.
        var decoded=uploads.Select(i=>(Input:i,Bytes:Decode(i),Name:WorkTask.Required(i.Name,200))).ToArray();
        if(existingBytes+decoded.Sum(i=>i.Bytes.Length)>MaxDiscussionBytes)throw new ArgumentException("此對話圖片總量上限 20 MB，請開新對話，以免超出 CLI 圖片輸入大小。");
        var saved=new List<DiscussionImage>();
        try{
            foreach(var image in decoded){
                var item=new DiscussionImage(Guid.NewGuid(),image.Name,image.Input.MimeType,image.Bytes.Length);
                saved.Add(item);var path=PathFor(discussion,item.Id);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path,image.Bytes,ct);
            }
            return saved.ToArray();
        }catch{Remove(discussion,saved);throw;}
    }
    public void Remove(Guid discussion,IEnumerable<DiscussionImage> images){foreach(var image in images)File.Delete(PathFor(discussion,image.Id));}
    public async Task<byte[]> ReadAsync(Guid discussion,DiscussionImage image,CancellationToken ct)
    {
        var path=PathFor(discussion,image.Id);
        if(!File.Exists(path))throw new MissingEntityException("圖片附件已遺失，請重新附上圖片。");
        var length=new FileInfo(path).Length;
        if(length>MaxBytes||length!=image.Bytes)throw new ArgumentException("圖片附件大小不符，請重新附上。");
        var bytes=await File.ReadAllBytesAsync(path,ct);Decode(new(image.Name,image.MimeType,Convert.ToBase64String(bytes)));return bytes;
    }
}
