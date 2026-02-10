using Pfim;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SoulsFormats;
using StudioCore.Application;
using StudioCore.Editors.ModelEditor;
using StudioCore.Renderer;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace StudioCore.Editors.Common;

public enum FlverGlbFaceSetMode
{
    FirstOnly
}

public sealed class FlverGlbExportOptions
{
    public bool IncludeFolder { get; set; }
    public bool EmbedPngTextures { get; set; } = true;
    public bool ExportSkeleton { get; set; }
    public FlverGlbFaceSetMode FaceSetMode { get; set; } = FlverGlbFaceSetMode.FirstOnly;
}

public sealed class FlverGlbTextureEntry
{
    public string Name { get; }
    public string VirtualPath { get; }
    public string MaterialString { get; }
    public MTD MTD { get; }
    public MATBIN MATBIN { get; }

    public FlverGlbTextureEntry(string name, string virtualPath, string materialString, MTD mtd, MATBIN matbin)
    {
        Name = name ?? string.Empty;
        VirtualPath = virtualPath ?? string.Empty;
        MaterialString = materialString ?? string.Empty;
        MTD = mtd;
        MATBIN = matbin;
    }
}

public sealed class GlbExportResult
{
    public bool Success { get; }
    public string OutputPath { get; }
    public string Error { get; }
    public IReadOnlyList<string> Warnings { get; }

    private GlbExportResult(bool success, string outputPath, string error, List<string> warnings)
    {
        Success = success;
        OutputPath = outputPath;
        Error = error;
        Warnings = warnings;
    }

    public static GlbExportResult Ok(string outputPath, List<string> warnings)
    {
        return new GlbExportResult(true, outputPath, string.Empty, warnings);
    }

    public static GlbExportResult Fail(string outputPath, string error, List<string> warnings)
    {
        return new GlbExportResult(false, outputPath, error, warnings);
    }
}

public static class FlverGlbExporter
{
    private const int ArrayBuffer = 34962;
    private const int ElementArrayBuffer = 34963;
    private const int FloatType = 5126;
    private const int UnsignedIntType = 5125;

    public static GlbExportResult Export(
        ProjectEntry project,
        string flverVirtualPath,
        FLVER2 flver,
        IReadOnlyList<FlverGlbTextureEntry> textureEntries,
        string outputDirectory,
        string modelName,
        FlverGlbExportOptions options)
    {
        List<string> warnings = new();
        var safeName = SanitizeFileName(modelName);
        var writeDir = outputDirectory;

        if (options.IncludeFolder)
        {
            writeDir = Path.Combine(outputDirectory, safeName);
        }

        if (!Directory.Exists(writeDir))
        {
            Directory.CreateDirectory(writeDir);
        }

        var outputPath = Path.Combine(writeDir, $"{safeName}.glb");

        if (options.ExportSkeleton)
        {
            warnings.Add("Skeleton export is not implemented yet; exporting static mesh only.");
        }

        if (!options.EmbedPngTextures)
        {
            warnings.Add("Only embedded PNG textures are supported in this exporter.");
        }

        try
        {
            var textureByKey = LoadTextures(project, textureEntries, warnings);
            var materialLookup = BuildMaterialLookup(project, flver, textureEntries);
            var glbData = BuildGlb(
                project,
                flverVirtualPath,
                flver,
                safeName,
                textureByKey,
                materialLookup,
                options,
                warnings);

            if (glbData == null)
            {
                return GlbExportResult.Fail(outputPath, "No exportable mesh data was found.", warnings);
            }

            File.WriteAllBytes(outputPath, glbData);
            return GlbExportResult.Ok(outputPath, warnings);
        }
        catch (Exception ex)
        {
            warnings.Add(ex.Message);
            return GlbExportResult.Fail(outputPath, "GLB export failed.", warnings);
        }
    }

    private static Dictionary<string, TextureData> LoadTextures(ProjectEntry project, IReadOnlyList<FlverGlbTextureEntry> entries, List<string> warnings)
    {
        Dictionary<string, TextureData> textures = new(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            var key = NormalizeTextureKey(entry.Name);
            if (string.IsNullOrWhiteSpace(key) || textures.ContainsKey(key))
            {
                continue;
            }

            if (!TryReadTextureDds(project, entry, key, out var ddsBytes))
            {
                warnings.Add($"Texture source not found: {entry.Name}");
                continue;
            }

            if (!TryConvertDdsToPng(ddsBytes, out var pngBytes))
            {
                warnings.Add($"Failed to decode DDS: {entry.Name}");
                continue;
            }

            textures[key] = new TextureData(key, pngBytes);
        }

        return textures;
    }

    private static Dictionary<string, MaterialLookup> BuildMaterialLookup(ProjectEntry project, FLVER2 flver, IReadOnlyList<FlverGlbTextureEntry> textureEntries)
    {
        Dictionary<string, MaterialLookup> lookup = new(StringComparer.OrdinalIgnoreCase);
        var bank = project?.Handler?.MaterialData?.PrimaryBank;

        // Build primary lookup from FLVER materials to avoid missing entries when texture list is deduplicated.
        foreach (var mat in flver.Materials)
        {
            var key = Path.GetFileNameWithoutExtension(mat.MTD ?? string.Empty);
            if (string.IsNullOrWhiteSpace(key) || lookup.ContainsKey(key))
            {
                continue;
            }

            MTD mtd = null;
            MATBIN matbin = null;

            if (bank != null)
            {
                mtd = bank.GetMaterial(key);

                if (project.Descriptor.ProjectType is ProjectType.ER or ProjectType.AC6 or ProjectType.NR)
                {
                    matbin = bank.GetMatbin(key);
                }
            }

            if (mtd != null || matbin != null)
            {
                lookup[key] = new MaterialLookup(mtd, matbin);
            }
        }

        // Fallback from insight texture entries (older behavior), fills gaps when bank data is unavailable.
        foreach (var entry in textureEntries)
        {
            var key = Path.GetFileNameWithoutExtension(entry.MaterialString ?? string.Empty);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (!lookup.ContainsKey(key))
            {
                lookup[key] = new MaterialLookup(entry.MTD, entry.MATBIN);
            }
        }

        return lookup;
    }

    private static bool TryReadTextureDds(ProjectEntry project, FlverGlbTextureEntry entry, string textureKey, out byte[] ddsBytes)
    {
        ddsBytes = null;

        if (string.IsNullOrWhiteSpace(entry.VirtualPath))
        {
            return false;
        }

        return TryReadTextureDds(project, entry.VirtualPath, textureKey, out ddsBytes);
    }

    private static bool TryReadTextureDds(ProjectEntry project, string textureVirtualPath, string textureKey, out byte[] ddsBytes)
    {
        ddsBytes = null;
        if (string.IsNullOrWhiteSpace(textureVirtualPath))
        {
            return false;
        }

        var relativePath = PathBuilder.GetRelativePath(project, textureVirtualPath);
        var fileData = project.VFS.FS.ReadFile(relativePath);
        if (fileData == null)
        {
            return false;
        }

        var containerType = ModelEditorUtils.GetContainerTypeFromVirtualPath(project, textureVirtualPath);
        if (containerType is ResourceContainerType.None)
        {
            if (TryFindTextureInTpf(fileData.Value, textureKey, out ddsBytes))
            {
                return true;
            }
        }

        if (containerType is ResourceContainerType.BND)
        {
            if (project.Descriptor.ProjectType is ProjectType.DES or ProjectType.DS1 or ProjectType.DS1R)
            {
                var reader = new BND3Reader(fileData.Value);
                foreach (var file in reader.Files)
                {
                    var filename = file.Name.ToLower();
                    if (!filename.Contains(".tpf") && !filename.Contains(".tpf.dcx"))
                    {
                        continue;
                    }

                    var inner = reader.ReadFile(file);
                    if (TryFindTextureInTpf(inner, textureKey, out ddsBytes))
                    {
                        return true;
                    }
                }
            }
            else
            {
                var reader = new BND4Reader(fileData.Value);
                foreach (var file in reader.Files)
                {
                    var filename = file.Name.ToLower();
                    if (!filename.Contains(".tpf") && !filename.Contains(".tpf.dcx"))
                    {
                        continue;
                    }

                    var inner = reader.ReadFile(file);
                    if (TryFindTextureInTpf(inner, textureKey, out ddsBytes))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool TryFindTextureInTpf(Memory<byte> tpfBytes, string textureKey, out byte[] ddsBytes)
    {
        ddsBytes = null;

        TPF tpf;
        try
        {
            tpf = TPF.Read(tpfBytes);
        }
        catch
        {
            return false;
        }

        foreach (var tpfTex in tpf.Textures)
        {
            if (NormalizeTextureKey(tpfTex.Name) == textureKey)
            {
                ddsBytes = tpfTex.Bytes.ToArray();
                return true;
            }
        }

        return false;
    }

    private static bool TryConvertDdsToPng(byte[] ddsBytes, out byte[] pngBytes)
    {
        pngBytes = null;
        using var image = Pfim.Dds.Create(ddsBytes, new PfimConfig());
        if (image.Compressed)
        {
            image.Decompress();
        }

        using var ms = new MemoryStream();

        if (image.Format == ImageFormat.Rgba32)
        {
            using var rgba = Image.LoadPixelData<Bgra32>(image.Data, image.Width, image.Height);
            rgba.SaveAsPng(ms);
        }
        else if (image.Format == ImageFormat.Rgb24)
        {
            using var rgb = Image.LoadPixelData<Bgr24>(image.Data, image.Width, image.Height);
            rgb.SaveAsPng(ms);
        }
        else if (image.Format == ImageFormat.Rgba16)
        {
            using var l8 = Image.LoadPixelData<L8>(image.Data, image.Width, image.Height);
            l8.SaveAsPng(ms);
        }
        else if (image.Format == ImageFormat.Rgb8)
        {
            using var l8 = Image.LoadPixelData<L8>(image.Data, image.Width, image.Height);
            l8.SaveAsPng(ms);
        }
        else
        {
            return false;
        }

        pngBytes = ms.ToArray();
        return true;
    }

    private static byte[] BuildGlb(
        ProjectEntry project,
        string flverVirtualPath,
        FLVER2 flver,
        string modelName,
        Dictionary<string, TextureData> textures,
        Dictionary<string, MaterialLookup> materialLookup,
        FlverGlbExportOptions options,
        List<string> warnings)
    {
        var binary = new MemoryStream();
        var primitives = new List<Dictionary<string, object>>();
        var bufferViews = new List<Dictionary<string, object>>();
        var accessors = new List<Dictionary<string, object>>();
        var materials = new List<Dictionary<string, object>>();
        var images = new List<Dictionary<string, object>>();
        var samplers = new List<Dictionary<string, object>>();
        var textureDefs = new List<Dictionary<string, object>>();

        var textureIndexByHash = new Dictionary<string, int>(StringComparer.Ordinal);
        var textureIndexByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var textureVirtualPathByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var derivedNormalBySourceKey = new Dictionary<string, DerivedNormalInfo>(StringComparer.OrdinalIgnoreCase);
        var derivedBaseColorAlphaByPair = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var alphaAnalysisByTextureKey = new Dictionary<string, AlphaAnalysis>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < flver.Materials.Count; i++)
        {
            var mat = flver.Materials[i];
            var materialName = string.IsNullOrWhiteSpace(mat.Name) ? $"Material_{i}" : mat.Name;
            var materialKey = Path.GetFileNameWithoutExtension(mat.MTD ?? string.Empty);
            materialLookup.TryGetValue(materialKey, out var lookup);

            string baseColorKey = null;
            string normalKey = null;
            string mrKey = null;
            string opacityKey = null;

            foreach (var tex in mat.Textures)
            {
                var resolvedPath = ResolveTexturePath(project, tex, lookup);
                if (string.IsNullOrWhiteSpace(resolvedPath))
                {
                    continue;
                }

                var virtualPath = TextureLocator.GetFlverTextureVirtualPath(flverVirtualPath, resolvedPath.ToLower());
                var resolvedKey = NormalizeTextureKey(Path.GetFileName(virtualPath));
                if (string.IsNullOrWhiteSpace(resolvedKey))
                {
                    continue;
                }

                if (!textureVirtualPathByKey.ContainsKey(resolvedKey))
                {
                    textureVirtualPathByKey[resolvedKey] = virtualPath;
                }

                var paramName = (tex.ParamName ?? string.Empty).ToLower();
                if (baseColorKey == null && IsBaseColorParam(paramName))
                {
                    baseColorKey = resolvedKey;
                }
                else if (normalKey == null && IsNormalParam(paramName))
                {
                    normalKey = resolvedKey;
                }
                else if (mrKey == null && IsMetallicRoughnessParam(paramName))
                {
                    mrKey = resolvedKey;
                }
                else if (opacityKey == null && IsOpacityParam(paramName))
                {
                    opacityKey = resolvedKey;
                }
            }

            if (baseColorKey == null && mat.Textures.Count > 0)
            {
                foreach (var tex in mat.Textures)
                {
                    var resolvedPath = ResolveTexturePath(project, tex, lookup);
                    if (string.IsNullOrWhiteSpace(resolvedPath))
                    {
                        continue;
                    }

                    var virtualPath = TextureLocator.GetFlverTextureVirtualPath(flverVirtualPath, resolvedPath.ToLower());
                    var resolvedKey = NormalizeTextureKey(Path.GetFileName(virtualPath));
                    if (!string.IsNullOrWhiteSpace(resolvedKey))
                    {
                        if (!textureVirtualPathByKey.ContainsKey(resolvedKey))
                        {
                            textureVirtualPathByKey[resolvedKey] = virtualPath;
                        }

                        baseColorKey = resolvedKey;
                        break;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(normalKey) &&
                TryEnsureTextureData(normalKey, textures, textureVirtualPathByKey, project, out var sourceNormalTex))
            {
                if (!derivedNormalBySourceKey.TryGetValue(normalKey, out var derivedInfo))
                {
                    derivedInfo = CreateDerivedNormalTextures(normalKey, sourceNormalTex, textures, warnings);
                    derivedNormalBySourceKey[normalKey] = derivedInfo;
                }

                if (!string.IsNullOrWhiteSpace(derivedInfo.NormalTextureKey))
                {
                    normalKey = derivedInfo.NormalTextureKey;
                }

                if (string.IsNullOrWhiteSpace(mrKey) && derivedInfo.HasPackedRoughness)
                {
                    mrKey = derivedInfo.RoughnessTextureKey;
                }
            }

            if (!string.IsNullOrWhiteSpace(opacityKey))
            {
                baseColorKey = EnsureBaseColorWithOpacity(
                    baseColorKey,
                    opacityKey,
                    textures,
                    textureVirtualPathByKey,
                    project,
                    derivedBaseColorAlphaByPair,
                    warnings);
            }

            var matDef = new Dictionary<string, object>
            {
                ["name"] = materialName
            };

            var alphaMode = DetermineAlphaMode(
                mat,
                lookup,
                baseColorKey,
                opacityKey,
                textures,
                textureVirtualPathByKey,
                project,
                alphaAnalysisByTextureKey,
                out var alphaCutoff);
            if (alphaMode != "OPAQUE")
            {
                matDef["alphaMode"] = alphaMode;
                if (alphaMode == "MASK")
                {
                    matDef["alphaCutoff"] = alphaCutoff;
                }
            }

            var pbr = new Dictionary<string, object>
            {
                ["metallicFactor"] = 0.0f,
                ["roughnessFactor"] = 1.0f
            };

            var baseColorTextureIndex = EnsureTexture(baseColorKey, textures, textureVirtualPathByKey, project, binary, bufferViews, images, samplers, textureDefs, textureIndexByHash, textureIndexByKey);
            if (baseColorTextureIndex.HasValue)
            {
                pbr["baseColorTexture"] = new Dictionary<string, object>
                {
                    ["index"] = baseColorTextureIndex.Value
                };
            }

            var mrTextureIndex = EnsureTexture(mrKey, textures, textureVirtualPathByKey, project, binary, bufferViews, images, samplers, textureDefs, textureIndexByHash, textureIndexByKey);
            if (mrTextureIndex.HasValue)
            {
                pbr["metallicRoughnessTexture"] = new Dictionary<string, object>
                {
                    ["index"] = mrTextureIndex.Value
                };
            }

            matDef["pbrMetallicRoughness"] = pbr;

            var normalTextureIndex = EnsureTexture(normalKey, textures, textureVirtualPathByKey, project, binary, bufferViews, images, samplers, textureDefs, textureIndexByHash, textureIndexByKey);
            if (normalTextureIndex.HasValue)
            {
                matDef["normalTexture"] = new Dictionary<string, object>
                {
                    ["index"] = normalTextureIndex.Value
                };
            }

            materials.Add(matDef);
        }

        foreach (var mesh in flver.Meshes)
        {
            if (mesh.Vertices == null || mesh.Vertices.Count == 0)
            {
                continue;
            }

            FLVER2.FaceSet faceSet = null;
            if (options.FaceSetMode == FlverGlbFaceSetMode.FirstOnly)
            {
                faceSet = mesh.FaceSets.FirstOrDefault();
            }

            if (faceSet == null)
            {
                warnings.Add("Skipped mesh with no FaceSet.");
                continue;
            }

            var indices = faceSet.Triangulate(mesh.Vertices.Count < ushort.MaxValue);
            if (indices.Count < 3)
            {
                warnings.Add("Skipped mesh with no triangles after triangulation.");
                continue;
            }

            var vertexCount = mesh.Vertices.Count;
            var positions = new float[vertexCount * 3];
            var normals = new float[vertexCount * 3];
            var uvs = new float[vertexCount * 2];

            bool hasNormal = false;
            bool hasUv = false;

            var min = new[] { float.MaxValue, float.MaxValue, float.MaxValue };
            var max = new[] { float.MinValue, float.MinValue, float.MinValue };

            for (int i = 0; i < vertexCount; i++)
            {
                var v = mesh.Vertices[i];

                positions[(i * 3) + 0] = v.Position.X;
                positions[(i * 3) + 1] = v.Position.Y;
                positions[(i * 3) + 2] = v.Position.Z;

                min[0] = Math.Min(min[0], v.Position.X);
                min[1] = Math.Min(min[1], v.Position.Y);
                min[2] = Math.Min(min[2], v.Position.Z);
                max[0] = Math.Max(max[0], v.Position.X);
                max[1] = Math.Max(max[1], v.Position.Y);
                max[2] = Math.Max(max[2], v.Position.Z);

                if (v.Normal.LengthSquared() > 0.0f)
                {
                    hasNormal = true;
                }

                normals[(i * 3) + 0] = v.Normal.X;
                normals[(i * 3) + 1] = v.Normal.Y;
                normals[(i * 3) + 2] = v.Normal.Z;

                if (v.UVs != null && v.UVs.Count > 0)
                {
                    hasUv = true;
                    uvs[(i * 2) + 0] = v.UVs[0].X;
                    uvs[(i * 2) + 1] = v.UVs[0].Y;
                }
            }

            var positionAccessor = AddFloatAccessor(binary, bufferViews, accessors, positions, 3, ArrayBuffer, min, max);
            int? normalAccessor = null;
            int? uvAccessor = null;

            if (hasNormal)
            {
                normalAccessor = AddFloatAccessor(binary, bufferViews, accessors, normals, 3, ArrayBuffer, null, null);
            }

            if (hasUv)
            {
                uvAccessor = AddFloatAccessor(binary, bufferViews, accessors, uvs, 2, ArrayBuffer, null, null);
            }

            var indexArray = BuildFlippedWindingIndices(indices);
            var indexAccessor = AddUIntAccessor(binary, bufferViews, accessors, indexArray, ElementArrayBuffer);

            var attributes = new Dictionary<string, object>
            {
                ["POSITION"] = positionAccessor
            };

            if (normalAccessor.HasValue)
            {
                attributes["NORMAL"] = normalAccessor.Value;
            }

            if (uvAccessor.HasValue)
            {
                attributes["TEXCOORD_0"] = uvAccessor.Value;
            }

            var primitive = new Dictionary<string, object>
            {
                ["attributes"] = attributes,
                ["indices"] = indexAccessor
            };

            if (mesh.MaterialIndex >= 0 && mesh.MaterialIndex < materials.Count)
            {
                primitive["material"] = mesh.MaterialIndex;
            }

            primitives.Add(primitive);
        }

        if (primitives.Count == 0)
        {
            return null;
        }

        var meshDef = new Dictionary<string, object>
        {
            ["name"] = modelName,
            ["primitives"] = primitives
        };

        var root = new Dictionary<string, object>
        {
            ["asset"] = new Dictionary<string, object>
            {
                ["version"] = "2.0",
                ["generator"] = "Smithbox FLVER GLB Exporter"
            },
            ["scene"] = 0,
            ["scenes"] = new List<object>
            {
                new Dictionary<string, object>
                {
                    ["nodes"] = new List<int> { 0 }
                }
            },
            ["nodes"] = new List<object>
            {
                new Dictionary<string, object>
                {
                    ["name"] = modelName,
                    ["mesh"] = 0
                }
            },
            ["meshes"] = new List<object> { meshDef },
            ["buffers"] = new List<object>
            {
                new Dictionary<string, object>
                {
                    ["byteLength"] = (int)binary.Length
                }
            },
            ["bufferViews"] = bufferViews,
            ["accessors"] = accessors
        };

        if (materials.Count > 0)
        {
            root["materials"] = materials;
        }

        if (textureDefs.Count > 0)
        {
            root["images"] = images;
            root["samplers"] = samplers;
            root["textures"] = textureDefs;
        }

        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = false
        };

        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(root, jsonOptions);
        return WriteGlb(jsonBytes, binary.ToArray());
    }

    private static int? EnsureTexture(
        string key,
        Dictionary<string, TextureData> textures,
        Dictionary<string, string> textureVirtualPathByKey,
        ProjectEntry project,
        MemoryStream binary,
        List<Dictionary<string, object>> bufferViews,
        List<Dictionary<string, object>> images,
        List<Dictionary<string, object>> samplers,
        List<Dictionary<string, object>> textureDefs,
        Dictionary<string, int> textureIndexByHash,
        Dictionary<string, int> textureIndexByKey)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        if (textureIndexByKey.TryGetValue(key, out var existingTextureIndex))
        {
            return existingTextureIndex;
        }

        if (!TryEnsureTextureData(key, textures, textureVirtualPathByKey, project, out var texture))
        {
            return null;
        }

        var hash = Convert.ToHexString(SHA256.HashData(texture.PngBytes));
        if (textureIndexByHash.TryGetValue(hash, out var existingByHash))
        {
            textureIndexByKey[key] = existingByHash;
            return existingByHash;
        }

        if (samplers.Count == 0)
        {
            samplers.Add(new Dictionary<string, object>
            {
                ["magFilter"] = 9729,
                ["minFilter"] = 9987,
                ["wrapS"] = 10497,
                ["wrapT"] = 10497
            });
        }

        var imageBufferView = AddRawBuffer(binary, bufferViews, texture.PngBytes, null);

        var imageIndex = images.Count;
        images.Add(new Dictionary<string, object>
        {
            ["name"] = texture.Key,
            ["bufferView"] = imageBufferView,
            ["mimeType"] = "image/png"
        });

        var textureIndex = textureDefs.Count;
        textureDefs.Add(new Dictionary<string, object>
        {
            ["sampler"] = 0,
            ["source"] = imageIndex,
            ["name"] = texture.Key
        });

        textureIndexByHash[hash] = textureIndex;
        textureIndexByKey[key] = textureIndex;
        return textureIndex;
    }

    private static bool TryEnsureTextureData(
        string key,
        Dictionary<string, TextureData> textures,
        Dictionary<string, string> textureVirtualPathByKey,
        ProjectEntry project,
        out TextureData texture)
    {
        texture = null;
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        if (textures.TryGetValue(key, out texture))
        {
            return true;
        }

        if (textureVirtualPathByKey.TryGetValue(key, out var textureVirtualPath) &&
            TryReadTextureDds(project, textureVirtualPath, key, out var ddsBytes) &&
            TryConvertDdsToPng(ddsBytes, out var pngBytes))
        {
            texture = new TextureData(key, pngBytes);
            textures[key] = texture;
            return true;
        }

        return false;
    }

    private static int AddFloatAccessor(
        MemoryStream binary,
        List<Dictionary<string, object>> bufferViews,
        List<Dictionary<string, object>> accessors,
        float[] values,
        int tupleSize,
        int target,
        float[] min,
        float[] max)
    {
        var bytes = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);

        var viewIndex = AddRawBuffer(binary, bufferViews, bytes, target);
        var accessor = new Dictionary<string, object>
        {
            ["bufferView"] = viewIndex,
            ["componentType"] = FloatType,
            ["count"] = values.Length / tupleSize,
            ["type"] = tupleSize == 3 ? "VEC3" : "VEC2"
        };

        if (min != null && max != null)
        {
            accessor["min"] = min;
            accessor["max"] = max;
        }

        var accessorIndex = accessors.Count;
        accessors.Add(accessor);
        return accessorIndex;
    }

    private static int AddUIntAccessor(
        MemoryStream binary,
        List<Dictionary<string, object>> bufferViews,
        List<Dictionary<string, object>> accessors,
        uint[] values,
        int target)
    {
        var bytes = new byte[values.Length * sizeof(uint)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);

        var viewIndex = AddRawBuffer(binary, bufferViews, bytes, target);
        var accessor = new Dictionary<string, object>
        {
            ["bufferView"] = viewIndex,
            ["componentType"] = UnsignedIntType,
            ["count"] = values.Length,
            ["type"] = "SCALAR"
        };

        var accessorIndex = accessors.Count;
        accessors.Add(accessor);
        return accessorIndex;
    }

    private static uint[] BuildFlippedWindingIndices(List<int> indices)
    {
        var result = new uint[indices.Count];

        for (int i = 0; i < indices.Count; i += 3)
        {
            if (i + 2 >= indices.Count)
            {
                break;
            }

            // glTF expects CCW front faces; FLVER triangles appear clockwise here.
            result[i + 0] = (uint)indices[i + 0];
            result[i + 1] = (uint)indices[i + 2];
            result[i + 2] = (uint)indices[i + 1];
        }

        return result;
    }

    private static int AddRawBuffer(
        MemoryStream binary,
        List<Dictionary<string, object>> bufferViews,
        byte[] bytes,
        int? target)
    {
        AlignStream(binary, 4, 0);
        var offset = (int)binary.Position;
        binary.Write(bytes, 0, bytes.Length);

        var view = new Dictionary<string, object>
        {
            ["buffer"] = 0,
            ["byteOffset"] = offset,
            ["byteLength"] = bytes.Length
        };

        if (target.HasValue)
        {
            view["target"] = target.Value;
        }

        var viewIndex = bufferViews.Count;
        bufferViews.Add(view);
        return viewIndex;
    }

    private static byte[] WriteGlb(byte[] jsonBytes, byte[] binBytes)
    {
        var paddedJsonLength = AlignLength(jsonBytes.Length, 4);
        var paddedBinLength = AlignLength(binBytes.Length, 4);
        var totalLength = 12 + 8 + paddedJsonLength + 8 + paddedBinLength;

        using var ms = new MemoryStream(totalLength);
        using var bw = new BinaryWriter(ms);

        bw.Write(0x46546C67);
        bw.Write(2);
        bw.Write(totalLength);

        bw.Write(paddedJsonLength);
        bw.Write(0x4E4F534A);
        bw.Write(jsonBytes);
        for (int i = jsonBytes.Length; i < paddedJsonLength; i++)
        {
            bw.Write((byte)0x20);
        }

        bw.Write(paddedBinLength);
        bw.Write(0x004E4942);
        bw.Write(binBytes);
        for (int i = binBytes.Length; i < paddedBinLength; i++)
        {
            bw.Write((byte)0);
        }

        return ms.ToArray();
    }

    private static int AlignLength(int value, int alignment)
    {
        var mod = value % alignment;
        return mod == 0 ? value : value + (alignment - mod);
    }

    private static void AlignStream(Stream stream, int alignment, byte padByte)
    {
        var mod = (int)(stream.Position % alignment);
        if (mod == 0)
        {
            return;
        }

        var pad = alignment - mod;
        for (int i = 0; i < pad; i++)
        {
            stream.WriteByte(padByte);
        }
    }

    private static string ResolveTexturePath(ProjectEntry project, FLVER2.Texture texture, MaterialLookup lookup)
    {
        if (texture == null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(texture.Path))
        {
            return texture.Path;
        }

        var type = texture.ParamName;
        if (string.IsNullOrWhiteSpace(type))
        {
            return null;
        }

        if (project.Descriptor.ProjectType is ProjectType.ER or ProjectType.AC6 or ProjectType.NR)
        {
            var fromMatbin = ResolveFromMatbin(type, lookup?.MATBIN);
            if (!string.IsNullOrWhiteSpace(fromMatbin))
            {
                return fromMatbin;
            }
        }

        return ResolveFromMtd(type, lookup?.MTD);
    }

    private static string ResolveFromMtd(string type, MTD mtd)
    {
        if (mtd == null)
        {
            return null;
        }

        var tex = mtd.Textures.Find(x => x.Type == type);
        if (tex == null || !tex.Extended || string.IsNullOrWhiteSpace(tex.Path))
        {
            return null;
        }

        return tex.Path;
    }

    private static string ResolveFromMatbin(string type, MATBIN matbin)
    {
        if (matbin == null)
        {
            return null;
        }

        foreach (var sampler in matbin.Samplers)
        {
            if (sampler.Type == type && !string.IsNullOrWhiteSpace(sampler.Path))
            {
                return sampler.Path;
            }
        }

        foreach (var sampler in matbin.Samplers)
        {
            if (string.IsNullOrWhiteSpace(type))
            {
                continue;
            }

            if (sampler.Type.Contains("__") && type.Contains("__"))
            {
                var samplerType = sampler.Type.Split("__")[1];
                var sourceType = type.Split("__")[1];
                if (samplerType == sourceType && !string.IsNullOrWhiteSpace(sampler.Path))
                {
                    return sampler.Path;
                }
            }

            if ((type == "g_DiffuseTexture" || type.Contains("AlbedoMap")) &&
                sampler.Type.Contains("AlbedoMap") &&
                !string.IsNullOrWhiteSpace(sampler.Path))
            {
                return sampler.Path;
            }

            if ((type == "g_BumpmapTexture" || type.Contains("NormalMap")) &&
                sampler.Type.Contains("NormalMap") &&
                !string.IsNullOrWhiteSpace(sampler.Path))
            {
                return sampler.Path;
            }
        }

        return null;
    }

    private static DerivedNormalInfo CreateDerivedNormalTextures(
        string sourceKey,
        TextureData sourceTexture,
        Dictionary<string, TextureData> textures,
        List<string> warnings)
    {
        if (!TryConvertPackedNormalTexture(sourceTexture.PngBytes, out var normalPng, out var roughnessPng, out var hasPackedRoughness))
        {
            warnings.Add($"Failed to post-process normal texture: {sourceKey}");
            return new DerivedNormalInfo(sourceKey, null, false);
        }

        var derivedNormalKey = $"{sourceKey}__glb_normal";
        textures[derivedNormalKey] = new TextureData(derivedNormalKey, normalPng);

        string roughnessKey = null;
        if (hasPackedRoughness && roughnessPng != null)
        {
            roughnessKey = $"{sourceKey}__glb_roughness";
            textures[roughnessKey] = new TextureData(roughnessKey, roughnessPng);
        }

        return new DerivedNormalInfo(derivedNormalKey, roughnessKey, hasPackedRoughness);
    }

    private static bool TryConvertPackedNormalTexture(byte[] sourcePng, out byte[] normalPng, out byte[] roughnessPng, out bool hasPackedRoughness)
    {
        normalPng = null;
        roughnessPng = null;
        hasPackedRoughness = true;

        try
        {
            using var source = Image.Load<Rgba32>(sourcePng);
            using var normalImage = new Image<Rgba32>(source.Width, source.Height);
            using var roughnessImage = new Image<Rgba32>(source.Width, source.Height);

            for (int y = 0; y < source.Height; y++)
            {
                for (int x = 0; x < source.Width; x++)
                {
                    var src = source[x, y];

                    var nx = (src.R / 255f) * 2f - 1f;
                    var ny = (src.G / 255f) * 2f - 1f;
                    var nzSq = 1f - (nx * nx) - (ny * ny);
                    var nz = nzSq > 0f ? MathF.Sqrt(nzSq) : 0f;

                    var outR = ToByte((nx * 0.5f) + 0.5f);
                    var outG = ToByte((ny * 0.5f) + 0.5f);
                    var outB = ToByte((nz * 0.5f) + 0.5f);

                    normalImage[x, y] = new Rgba32(outR, outG, outB, 255);

                    // glTF roughness is stored in G channel; source uses inverse roughness.
                    var roughness = (byte)(255 - src.B);
                    roughnessImage[x, y] = new Rgba32(0, roughness, 0, 255);
                }
            }

            using (var ms = new MemoryStream())
            {
                normalImage.SaveAsPng(ms);
                normalPng = ms.ToArray();
            }

            using var roughnessStream = new MemoryStream();
            roughnessImage.SaveAsPng(roughnessStream);
            roughnessPng = roughnessStream.ToArray();

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static byte ToByte(float value)
    {
        var clamped = Math.Clamp(value, 0f, 1f);
        return (byte)MathF.Round(clamped * 255f);
    }

    private static string EnsureBaseColorWithOpacity(
        string baseColorKey,
        string opacityKey,
        Dictionary<string, TextureData> textures,
        Dictionary<string, string> textureVirtualPathByKey,
        ProjectEntry project,
        Dictionary<string, string> derivedBaseColorAlphaByPair,
        List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(opacityKey))
        {
            return baseColorKey;
        }

        if (string.IsNullOrWhiteSpace(baseColorKey))
        {
            return opacityKey;
        }

        if (baseColorKey.Equals(opacityKey, StringComparison.OrdinalIgnoreCase))
        {
            return baseColorKey;
        }

        var pairKey = $"{baseColorKey}|{opacityKey}";
        if (derivedBaseColorAlphaByPair.TryGetValue(pairKey, out var existingCompositeKey))
        {
            return existingCompositeKey;
        }

        if (!TryEnsureTextureData(baseColorKey, textures, textureVirtualPathByKey, project, out var baseColorTexture) ||
            !TryEnsureTextureData(opacityKey, textures, textureVirtualPathByKey, project, out var opacityTexture))
        {
            return baseColorKey;
        }

        if (!TryMergeBaseColorAndOpacity(baseColorTexture.PngBytes, opacityTexture.PngBytes, out var mergedPngBytes))
        {
            warnings.Add($"Failed to merge opacity map into base color: {baseColorKey} + {opacityKey}");
            return baseColorKey;
        }

        var compositeKey = $"{baseColorKey}__glb_alpha_{opacityKey}";
        textures[compositeKey] = new TextureData(compositeKey, mergedPngBytes);
        derivedBaseColorAlphaByPair[pairKey] = compositeKey;
        return compositeKey;
    }

    private static bool TryMergeBaseColorAndOpacity(byte[] baseColorPng, byte[] opacityPng, out byte[] mergedPngBytes)
    {
        mergedPngBytes = null;

        try
        {
            using var baseColorImage = Image.Load<Rgba32>(baseColorPng);
            using var opacityImage = Image.Load<Rgba32>(opacityPng);
            if (baseColorImage.Width != opacityImage.Width || baseColorImage.Height != opacityImage.Height)
            {
                return false;
            }

            var useOpacityAlpha = HasAnyNonOpaqueAlpha(opacityImage);
            using var merged = new Image<Rgba32>(baseColorImage.Width, baseColorImage.Height);
            for (int y = 0; y < baseColorImage.Height; y++)
            {
                for (int x = 0; x < baseColorImage.Width; x++)
                {
                    var basePixel = baseColorImage[x, y];
                    var opacityPixel = opacityImage[x, y];
                    var alpha = useOpacityAlpha ? opacityPixel.A : opacityPixel.R;
                    merged[x, y] = new Rgba32(basePixel.R, basePixel.G, basePixel.B, alpha);
                }
            }

            using var ms = new MemoryStream();
            merged.SaveAsPng(ms);
            mergedPngBytes = ms.ToArray();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string DetermineAlphaMode(
        FLVER2.Material material,
        MaterialLookup lookup,
        string baseColorKey,
        string opacityKey,
        Dictionary<string, TextureData> textures,
        Dictionary<string, string> textureVirtualPathByKey,
        ProjectEntry project,
        Dictionary<string, AlphaAnalysis> alphaAnalysisByTextureKey,
        out float alphaCutoff)
    {
        alphaCutoff = 0.5f;

        if (TryGetBlendMode(lookup, out var blendMode))
        {
            if (blendMode == (int)MTD.BlendMode.TexEdge || blendMode == (int)MTD.BlendMode.LSTexEdge)
            {
                return "MASK";
            }

            if (blendMode != (int)MTD.BlendMode.Normal && blendMode != (int)MTD.BlendMode.LSNormal)
            {
                return "BLEND";
            }
        }

        if (TryGetAlphaTestSettings(lookup, out var hasAlphaTest, out var cutoffFromParams) && hasAlphaTest)
        {
            alphaCutoff = cutoffFromParams;
            return "MASK";
        }

        if (!string.IsNullOrWhiteSpace(opacityKey))
        {
            if (IsMaskMaterialHint(material, lookup))
            {
                return "MASK";
            }

            return "BLEND";
        }

        if (!string.IsNullOrWhiteSpace(baseColorKey) &&
            TryGetAlphaAnalysis(baseColorKey, textures, textureVirtualPathByKey, project, alphaAnalysisByTextureKey, out var alphaAnalysis) &&
            alphaAnalysis.HasTransparency)
        {
            return alphaAnalysis.IsBinaryAlpha ? "MASK" : "BLEND";
        }

        return "OPAQUE";
    }

    private static bool TryGetBlendMode(MaterialLookup lookup, out int blendMode)
    {
        blendMode = 0;

        foreach (var param in EnumerateMaterialParams(lookup))
        {
            var paramName = param.Key.ToLowerInvariant();
            if (!paramName.Contains("blendmode"))
            {
                continue;
            }

            if (TryGetScalarInt(param.Value, out blendMode))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetAlphaTestSettings(MaterialLookup lookup, out bool hasAlphaTest, out float alphaCutoff)
    {
        hasAlphaTest = false;
        alphaCutoff = 0.5f;

        foreach (var param in EnumerateMaterialParams(lookup))
        {
            var paramName = param.Key.ToLowerInvariant();
            var isAlphaTestFlag = paramName.Contains("alphatest") || paramName.Contains("alphaclip");
            var isAlphaCutoff = (paramName.Contains("alpha") || paramName.Contains("clip")) &&
                                (paramName.Contains("cutoff") || paramName.Contains("threshold"));

            if (isAlphaTestFlag && TryGetScalarBool(param.Value, out var enabled) && enabled)
            {
                hasAlphaTest = true;
            }

            if (isAlphaCutoff && TryGetScalarFloat(param.Value, out var cutoff))
            {
                hasAlphaTest = true;
                alphaCutoff = NormalizeAlphaCutoff(cutoff);
            }
        }

        return hasAlphaTest;
    }

    private static bool IsMaskMaterialHint(FLVER2.Material material, MaterialLookup lookup)
    {
        var text = string.Join(" ",
            material?.Name ?? string.Empty,
            material?.MTD ?? string.Empty,
            lookup?.MATBIN?.ShaderPath ?? string.Empty,
            lookup?.MATBIN?.SourcePath ?? string.Empty).ToLowerInvariant();

        return text.Contains("alphatest") ||
               text.Contains("cutout") ||
               text.Contains("clip") ||
               text.Contains("texedge");
    }

    private static bool TryGetAlphaAnalysis(
        string textureKey,
        Dictionary<string, TextureData> textures,
        Dictionary<string, string> textureVirtualPathByKey,
        ProjectEntry project,
        Dictionary<string, AlphaAnalysis> alphaAnalysisByTextureKey,
        out AlphaAnalysis analysis)
    {
        if (alphaAnalysisByTextureKey.TryGetValue(textureKey, out analysis))
        {
            return true;
        }

        analysis = default;
        if (!TryEnsureTextureData(textureKey, textures, textureVirtualPathByKey, project, out var texture))
        {
            return false;
        }

        if (!TryAnalyzeAlpha(texture.PngBytes, out analysis))
        {
            return false;
        }

        alphaAnalysisByTextureKey[textureKey] = analysis;
        return true;
    }

    private static bool TryAnalyzeAlpha(byte[] pngBytes, out AlphaAnalysis analysis)
    {
        analysis = default;

        try
        {
            using var image = Image.Load<Rgba32>(pngBytes);
            bool hasTransparency = false;
            bool isBinaryAlpha = true;

            for (int y = 0; y < image.Height; y++)
            {
                for (int x = 0; x < image.Width; x++)
                {
                    var alpha = image[x, y].A;
                    if (alpha < 255)
                    {
                        hasTransparency = true;
                    }

                    if (alpha != 0 && alpha != 255)
                    {
                        isBinaryAlpha = false;
                    }

                    if (hasTransparency && !isBinaryAlpha)
                    {
                        break;
                    }
                }

                if (hasTransparency && !isBinaryAlpha)
                {
                    break;
                }
            }

            analysis = new AlphaAnalysis(hasTransparency, hasTransparency && isBinaryAlpha);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasAnyNonOpaqueAlpha(Image<Rgba32> image)
    {
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                if (image[x, y].A < 255)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static float NormalizeAlphaCutoff(float value)
    {
        if (value > 1.0f && value <= 255.0f)
        {
            value /= 255.0f;
        }

        return Math.Clamp(value, 0.0f, 1.0f);
    }

    private static IEnumerable<KeyValuePair<string, object>> EnumerateMaterialParams(MaterialLookup lookup)
    {
        if (lookup?.MTD?.Params != null)
        {
            foreach (var param in lookup.MTD.Params)
            {
                if (param != null && !string.IsNullOrWhiteSpace(param.Name))
                {
                    yield return new KeyValuePair<string, object>(param.Name, param.Value);
                }
            }
        }

        if (lookup?.MATBIN?.Params != null)
        {
            foreach (var param in lookup.MATBIN.Params)
            {
                if (param != null && !string.IsNullOrWhiteSpace(param.Name))
                {
                    yield return new KeyValuePair<string, object>(param.Name, param.Value);
                }
            }
        }
    }

    private static bool TryGetScalarInt(object value, out int result)
    {
        result = 0;
        if (value == null)
        {
            return false;
        }

        switch (value)
        {
            case int i:
                result = i;
                return true;
            case uint ui:
                result = unchecked((int)ui);
                return true;
            case short s:
                result = s;
                return true;
            case ushort us:
                result = us;
                return true;
            case byte b:
                result = b;
                return true;
            case sbyte sb:
                result = sb;
                return true;
            case bool boolValue:
                result = boolValue ? 1 : 0;
                return true;
            case long l:
                result = (int)l;
                return true;
            case float f:
                result = (int)f;
                return true;
            case double d:
                result = (int)d;
                return true;
            case int[] intArray when intArray.Length > 0:
                result = intArray[0];
                return true;
            case float[] floatArray when floatArray.Length > 0:
                result = (int)floatArray[0];
                return true;
            default:
                return false;
        }
    }

    private static bool TryGetScalarFloat(object value, out float result)
    {
        result = 0.0f;
        if (value == null)
        {
            return false;
        }

        switch (value)
        {
            case float f:
                result = f;
                return true;
            case double d:
                result = (float)d;
                return true;
            case int i:
                result = i;
                return true;
            case uint ui:
                result = ui;
                return true;
            case short s:
                result = s;
                return true;
            case ushort us:
                result = us;
                return true;
            case byte b:
                result = b;
                return true;
            case sbyte sb:
                result = sb;
                return true;
            case bool boolValue:
                result = boolValue ? 1.0f : 0.0f;
                return true;
            case int[] intArray when intArray.Length > 0:
                result = intArray[0];
                return true;
            case float[] floatArray when floatArray.Length > 0:
                result = floatArray[0];
                return true;
            default:
                return false;
        }
    }

    private static bool TryGetScalarBool(object value, out bool result)
    {
        result = false;
        if (value == null)
        {
            return false;
        }

        switch (value)
        {
            case bool b:
                result = b;
                return true;
            default:
                if (TryGetScalarFloat(value, out var floatValue))
                {
                    result = floatValue > 0.0f;
                    return true;
                }

                return false;
        }
    }

    private static bool IsBaseColorParam(string paramName)
    {
        return paramName.Contains("g_diffuse") || paramName.Contains("albedo") || paramName.Contains("basecolor");
    }

    private static bool IsNormalParam(string paramName)
    {
        return paramName.Contains("g_bumpmap") || paramName.Contains("normal");
    }

    private static bool IsMetallicRoughnessParam(string paramName)
    {
        return paramName.Contains("metallicroughness") || paramName.Contains("roughness") || paramName.Contains("metallic");
    }

    private static bool IsOpacityParam(string paramName)
    {
        return paramName.Contains("opacity") ||
               paramName.Contains("alpha") ||
               paramName.Contains("transparency") ||
               paramName.Contains("dissolve");
    }

    private static string NormalizeTextureKey(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var normalized = input.Replace('\\', '/');
        var filename = Path.GetFileName(normalized);
        var withoutExt = Path.GetFileNameWithoutExtension(filename);
        return withoutExt.ToLowerInvariant();
    }

    private static string SanitizeFileName(string input)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = input ?? "model";
        foreach (var c in invalid)
        {
            result = result.Replace(c, '_');
        }

        return string.IsNullOrWhiteSpace(result) ? "model" : result;
    }

    private sealed class TextureData
    {
        public string Key { get; }
        public byte[] PngBytes { get; }

        public TextureData(string key, byte[] pngBytes)
        {
            Key = key;
            PngBytes = pngBytes;
        }
    }

    private sealed class MaterialLookup
    {
        public MTD MTD { get; }
        public MATBIN MATBIN { get; }

        public MaterialLookup(MTD mtd, MATBIN matbin)
        {
            MTD = mtd;
            MATBIN = matbin;
        }
    }

    private readonly struct AlphaAnalysis
    {
        public bool HasTransparency { get; }
        public bool IsBinaryAlpha { get; }

        public AlphaAnalysis(bool hasTransparency, bool isBinaryAlpha)
        {
            HasTransparency = hasTransparency;
            IsBinaryAlpha = isBinaryAlpha;
        }
    }

    private sealed class DerivedNormalInfo
    {
        public string NormalTextureKey { get; }
        public string RoughnessTextureKey { get; }
        public bool HasPackedRoughness { get; }

        public DerivedNormalInfo(string normalTextureKey, string roughnessTextureKey, bool hasPackedRoughness)
        {
            NormalTextureKey = normalTextureKey;
            RoughnessTextureKey = roughnessTextureKey;
            HasPackedRoughness = hasPackedRoughness;
        }
    }
}
