using System.Text.Json;

namespace ARI.LLM;

[Tool("edit_image", "Change a picture: resize, crop, rotate, flip, grayscale, brightness, contrast, blur, sharpen, add text, or convert between png, jpg, webp and gif. One edit per line, applied in order: " +
                    "resize 800x600 (or 800x to keep the shape), fit 1024 (longest side), crop x,y,width,height, rotate 90, flip h (or v), grayscale, brightness 1.3, contrast 1.2, blur 2, sharpen, " +
                    "text \"Hello\" at 20,20 size 36 color #ffffff, info (shows the size). The file's own type decides the format, so save as .jpg to convert. " +
                    "This is plain editing, not AI drawing; to generate or repaint a picture use generate_image. The original is never changed: the result goes to a new file.", Group = "image_tools")]
public class EditImage : Tool
{
    [Param("The picture to change, in your working folder or an absolute path.")]
    public string Path;

    [Param("The edits, one per line, in the formats above.")]
    public string Edits;

    [Param("Where to save the result, ending in .png, .jpg, .webp or .gif. Defaults to \"<name> - edited\" in the same format.", Optional = true)]
    public string SaveAs;

    public override async Task<string> Run()
    {
        string src = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(Path) ? Path : System.IO.Path.Combine(WorkingFolder, Path));
        if (!File.Exists(src)) return $"[Error: {src} doesn't exist.]";
        string srcExt = System.IO.Path.GetExtension(src);
        string name = string.IsNullOrWhiteSpace(SaveAs) ? System.IO.Path.GetFileNameWithoutExtension(src) + " - edited" + srcExt : SaveAs;
        string ext = System.IO.Path.GetExtension(name).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".gif")) return "[Error: save as .png, .jpg, .webp or .gif.]";
        string full = SaveRules.Resolve(WorkingFolder, name, ext, CallThread, out string? refusal);
        if (refusal is not null) return refusal;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);

        await DocumentVenv.EnsurePackage("pillow");
        string payload = JsonSerializer.Serialize(new { source = src, output = full, edits = (Edits ?? "").Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray() });
        (int code, string output, string err) = await DocumentVenv.RunScript(Source, payload, WorkingFolder, "image_edit.py");
        if (code != 0) return $"[Error: couldn't edit the picture: {(err.Length > 0 ? err.Trim().Split('\n').Last() : output.Trim())}]";
        if (!output.Contains("Result is")) return output.Trim();   // only "info" edits: nothing was saved
        SaveRules.Created(CallThread, full);
        return $"Saved {full}. {output.Trim()} Give it to the user with deliver_file, or look at it with read_file.";
    }

    private const string Source = """
import sys, json, re, os
from PIL import Image, ImageDraw, ImageEnhance, ImageFilter, ImageFont, ImageOps

cfg = json.load(sys.stdin)
img = ImageOps.exif_transpose(Image.open(cfg["source"]))
notes, changed = [], False

def font(size):
    for f in ("/System/Library/Fonts/Helvetica.ttc", "/System/Library/Fonts/Supplemental/Arial.ttf", "C:/Windows/Fonts/arial.ttf", "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"):
        if os.path.exists(f):
            try: return ImageFont.truetype(f, size)
            except Exception: pass
    return ImageFont.load_default(size)

for e in cfg["edits"]:
    low = e.lower()
    if low == "info": notes.append(f"{img.width}x{img.height} {img.mode}"); continue
    if low.startswith("resize"):
        m = re.match(r"resize\s+(\d*)\s*x\s*(\d*)$", low)
        if not m or not (m.group(1) or m.group(2)): raise SystemExit(f"bad edit: {e} (use: resize 800x600 or resize 800x)")
        w = int(m.group(1)) if m.group(1) else round(img.width * int(m.group(2)) / img.height)
        h = int(m.group(2)) if m.group(2) else round(img.height * int(m.group(1)) / img.width)
        img = img.resize((w, h), Image.LANCZOS)
    elif low.startswith("fit"):
        side = int(low.split()[1]); img.thumbnail((side, side), Image.LANCZOS)
    elif low.startswith("crop"):
        x, y, w, h = [int(v) for v in re.split(r"[,\s]+", low[4:].strip())]
        img = img.crop((x, y, min(x + w, img.width), min(y + h, img.height)))
    elif low.startswith("rotate"): img = img.rotate(-float(low.split()[1]), expand=True)
    elif low.startswith("flip"): img = ImageOps.mirror(img) if low.split()[1].startswith("h") else ImageOps.flip(img)
    elif low == "grayscale": img = ImageOps.grayscale(img)
    elif low.startswith("brightness"): img = ImageEnhance.Brightness(img.convert("RGB")).enhance(float(low.split()[1]))
    elif low.startswith("contrast"): img = ImageEnhance.Contrast(img.convert("RGB")).enhance(float(low.split()[1]))
    elif low.startswith("blur"): img = img.filter(ImageFilter.GaussianBlur(float(low.split()[1]) if len(low.split()) > 1 else 2))
    elif low == "sharpen": img = img.filter(ImageFilter.SHARPEN)
    elif low.startswith("text"):
        m = re.match(r'text\s+"(.*?)"\s*(?:at\s+(-?\d+)\s*,\s*(-?\d+))?\s*(?:size\s+(\d+))?\s*(?:color\s+(#[0-9a-fA-F]{3,8}|\w+))?\s*$', e, re.I)
        if not m: raise SystemExit(f'bad edit: {e} (use: text "Hello" at 20,20 size 36 color #ffffff)')
        img = img.convert("RGBA"); d = ImageDraw.Draw(img)
        d.text((int(m.group(2) or 20), int(m.group(3) or 20)), m.group(1), font=font(int(m.group(4) or 32)), fill=m.group(5) or "#ffffff", stroke_width=1, stroke_fill="#00000080")
    else: raise SystemExit(f"unknown edit: {e}")
    changed = True

if changed or not any(x.lower() == "info" for x in cfg["edits"]):
    out = cfg["output"]; ext = os.path.splitext(out)[1].lower()
    if ext in (".jpg", ".jpeg"): img = img.convert("RGB")
    elif ext == ".gif": img = img.convert("P")
    img.save(out, quality=90) if ext in (".jpg", ".jpeg", ".webp") else img.save(out)
    notes.append(f"Result is {img.width}x{img.height}.")
print(" ".join(notes))
""";
}
