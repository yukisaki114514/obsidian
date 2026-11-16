using System; using System.Collections.Generic; using System.IO; using System.Linq;
namespace FolderLabeler {
static class Tests {
  static int fail = 0, pass = 0;
  static void Check(bool c, string name) { if (c) { pass++; Console.WriteLine("PASS " + name); } else { fail++; Console.WriteLine("FAIL " + name); } }
  static MatchTarget T(string name, List<WalkedFile> f) => new MatchTarget{ folderName=name, contentCount=f.Count, extensions=new HashSet<string>(f.Select(x=>Path.GetExtension(x.rel))), ancestors=new string[0], guid=name };
  static List<RegistryCandidate> M(RegistryEntry e, string dir, bool meta) {
    var w = FolderWalker.Walk(dir, meta).files;
    return RegistryMatcher.Match(new[]{ T(Path.GetFileName(dir), w) }, new IList<WalkedFile>[]{ w }, new[]{ e }, new RegistrySettings())[0];
  }
  static void Show(string tag, List<RegistryCandidate> c) { foreach (var x in c) Console.WriteLine("   " + tag + ": exact=" + x.exact + " key=" + x.key + " entry=" + x.entryCoverage.ToString("0.00") + " folder=" + x.folderCoverage.ToString("0.00")); if (c.Count==0) Console.WriteLine("   " + tag + ": (no match)"); }
  static LabelResult Infer(string dir) {
    var files = FolderWalker.Walk(dir, true).files;
    var o = Inference.Run(new Inference.Context{ assetPath="Assets/x", folderName=Path.GetFileName(dir), files=files, settings=new RegistrySettings(), sdkPresent=false, prefabDescriptorCache=new Dictionary<string,bool>() });
    var r = Inference.Decide(o);
    Console.WriteLine("   " + Path.GetFileName(dir) + " -> " + r.label + " / " + r.confidence + " / " + r.layer + " | " + string.Join(" ; ", r.signals.Select(s => s.strength + ":" + s.text)));
    return r;
  }
  static int Main(string[] a) {
    string D = a[0], cf = a[1], sf = a[2];
    // 1. unitypackage reader
    string note;
    var entries = Fingerprints.FromUnityPackage(cf, "ColliderFit", "tool", out note);
    Check(entries.Count == 1, "unitypackage -> 1 entry (note=" + note + ")");
    var e = entries[0];
    Console.WriteLine("   files=" + e.fileCount + " bytes=" + e.totalBytes + " sample=" + e.files[0].p + " guid=" + e.files[0].g);
    Check(e.fileCount == 8 && e.files.All(f => f.g.Length == 32 && RegistryStore.IsSafeRelative(f.p) && !f.p.StartsWith("Assets")), "entry has 8 files, relative paths, guids");
    var sfe = Fingerprints.FromUnityPackage(sf, "ShapeFit", "tool", out note);
    Console.WriteLine("   ShapeFit entries=" + sfe.Count + " files=" + sfe.Sum(x=>x.fileCount) + " note=" + note);
    Check(sfe.Count >= 1, "ShapeFit unitypackage readable");
    // 2. same import (guid)
    var c = M(e, D + "/projA/Assets/ColliderFit", true); Show("A same", c);
    Check(c.Count == 1 && c[0].exact && c[0].key == "guid", "A: unitypackage import -> exact by GUID");
    // 3. renamed + user file
    c = M(e, D + "/projB/Assets/MyRenamed", true); Show("B renamed+userfile", c);
    Check(c.Count == 1 && !c[0].exact && c[0].entryCoverage >= 0.99f, "B: renamed + extra user file -> partial candidate (not auto-confirmed)");
    // 4. folder copy without meta
    c = M(e, D + "/projC/Assets/ColliderFit", true); Show("C copy-no-meta", c);
    Check(c.Count == 1 && c[0].exact && c[0].key == "path", "C: copy without .meta -> exact by relative path");
    // 5. edited file, no meta
    c = M(e, D + "/projD/Assets/ColliderFit", true); Show("D edited", c);
    Check(c.Count == 1 && !c[0].exact && c[0].entryCoverage >= 0.8f, "D: one file edited -> partial candidate");
    // 6. unrelated tool must not match
    c = M(e, D + "/fx/HandGestureFit", true); Show("HGF vs CF", c);
    Check(c.Count == 0, "unrelated folder -> no match");
    // 7. external folder fingerprint roundtrip (projA as external, matched against projC)
    var walkA = FolderWalker.Walk(D + "/projA/Assets/ColliderFit", true).files;
    var ext = Fingerprints.FromWalk(walkA, "CF ext", "tool", "external_folder", "ColliderFit");
    Check(ext.files.All(f => !f.p.Contains(":") && !f.p.StartsWith("/")), "external fingerprint holds no absolute paths");
    c = M(ext, D + "/projC/Assets/ColliderFit", true); Show("ext vs C", c);
    Check(c.Count == 1 && c[0].exact, "external-folder registration matches project copy");
    // 8. nested suppression: group containing product
    var gw = FolderWalker.Walk(D + "/projA/Assets", true).files; var pw = FolderWalker.Walk(D + "/projA/Assets/ColliderFit", true).files;
    var tg = T("Assets", gw); tg.guid = "G"; var tp = T("ColliderFit", pw); tp.guid = "P"; tp.ancestors = new[]{"G"};
    var res = RegistryMatcher.Match(new[]{tg,tp}, new IList<WalkedFile>[]{gw,pw}, new[]{e}, new RegistrySettings());
    Check(res[0].Count == 0 && res[1].Count == 1 && res[1][0].exact, "parent group suppressed when child matches");
    // 9. inference
    Check(Infer(D + "/fx/HandGestureFit").label == "tool" , "HandGestureFit source -> tool");
    Check(Infer(D + "/fx/HandGestureFit").confidence == "high", "  ... with high confidence");
    var sh = Infer(D + "/fx/Shaders"); Check(sh.label == "shader" && sh.confidence == "high", "shader folder -> shader/high");
    var em = Infer(D + "/fx/Empty"); Check(em.undetermined && em.label == "other" && em.signals.Any(s => s.text.Contains("根拠不足")), "empty folder -> undetermined with reason");
    var tx = Infer(D + "/fx/Tex"); Check(tx.label == "texture_material" && tx.confidence == "mid", "texture folder -> texture/mid");
    // 10. grouping
    string why;
    Check(Inference.LooksLikeGroup(D + "/fx/Author", "Author", out why), "Author/{ProductA,ProductB} -> group");
    Check(!Inference.LooksLikeGroup(D + "/fx/Product", "Product", out why), "Product/{Textures,FBX,Prefab} -> not group");
    // 11. builtin rules
    bool ok; var rule = RegistryMatcher.MatchBuiltin("AvatarAppearancePresets", new HashSet<string>{".cs"}, out ok);
    Check(rule != null && rule.id == "builtin.shapefit" && ok, "ShapeFit install folder -> builtin tool rule");
    rule = RegistryMatcher.MatchBuiltin("lilToon", new HashSet<string>{".png"}, out ok);
    Check(rule != null && !ok, "lilToon name without shaders -> rule hit but structure not ok (mid)");
    Check(RegistryMatcher.MatchBuiltin("MyOutfit", new HashSet<string>{".fbx"}, out ok) == null, "ordinary folder -> no builtin rule");
    // 12. decide conflict
    var o2 = new Inference.Output(); o2.evidence.Add(new Evidence{label=Label.Tool,strength=Strength.Strong,text="a"}); o2.evidence.Add(new Evidence{label=Label.Shader,strength=Strength.Strong,text="b"});
    Check(Inference.Decide(o2).confidence == "mid", "two strong signals on different labels -> mid");
    var o3 = new Inference.Output(); o3.evidence.Add(new Evidence{label=Label.Outfit,strength=Strength.Weak,text="kw"});
    Check(Inference.Decide(o3).confidence == "low", "weak only -> low");
    Console.WriteLine("\n" + pass + " passed, " + fail + " failed");
    return fail;
  }
}}
