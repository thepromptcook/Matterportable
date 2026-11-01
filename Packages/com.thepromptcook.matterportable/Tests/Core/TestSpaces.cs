using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Matterportable.Core;

namespace Matterportable.Tests
{
    /// <summary>Small hand-made spaces for viewer and measurement tests.</summary>
    public static class TestSpaces
    {
        /// <summary>A closed room, 4 m (x) by 3 m (z) by 2.5 m high, with faces pointing inwards, optionally rotated,
        /// plus optional boxes (furniture, steps, beams; not rotated).</summary>
        public static SpaceGeometry Room(double rotationDegrees = 0, params (Vec3 min, Vec3 max)[] boxes)
        {
            double r = rotationDegrees * Math.PI / 180;
            Vec3 P(float x, float y, float z) => new Vec3((float)(x * Math.Cos(r) - z * Math.Sin(r)), y, (float)(x * Math.Sin(r) + z * Math.Cos(r)));
            var positions = new List<Vec3>();
            var indices = new List<int>();
            var centre = P(2, 1.25f, 1.5f);
            Quad(positions, indices, P(0, 0, 0), P(4, 0, 0), P(4, 0, 3), P(0, 0, 3), centre);             // floor
            Quad(positions, indices, P(0, 2.5f, 0), P(4, 2.5f, 0), P(4, 2.5f, 3), P(0, 2.5f, 3), centre); // ceiling
            Quad(positions, indices, P(0, 0, 0), P(4, 0, 0), P(4, 2.5f, 0), P(0, 2.5f, 0), centre);       // wall z = 0
            Quad(positions, indices, P(0, 0, 3), P(4, 0, 3), P(4, 2.5f, 3), P(0, 2.5f, 3), centre);       // wall z = 3
            Quad(positions, indices, P(0, 0, 0), P(0, 0, 3), P(0, 2.5f, 3), P(0, 2.5f, 0), centre);       // wall x = 0
            Quad(positions, indices, P(4, 0, 0), P(4, 0, 3), P(4, 2.5f, 3), P(4, 2.5f, 0), centre);       // wall x = 4
            foreach (var (min, max) in boxes) Box(positions, indices, min, max);
            return Build(positions, indices);
        }

        /// <summary>Only boxes, floating in nothing (platforms with edges to walk off).</summary>
        public static SpaceGeometry Boxes(params (Vec3 min, Vec3 max)[] boxes)
        {
            var positions = new List<Vec3>();
            var indices = new List<int>();
            foreach (var (min, max) in boxes) Box(positions, indices, min, max);
            return Build(positions, indices);
        }

        /// <summary>
        /// A 16 × 16 grid (256 points, 450 triangles) with bumpy heights and UVs, Draco-compressed by Google's encoder
        /// (draco3d) the way Matterport's tiles are: sequential, 16-bit positions, 12-bit UVs, difference prediction. Its
        /// positions use the tagged symbol coding and its UVs the raw one. Point (i, j) is index j * 16 + i.
        /// </summary>
        public const string DracoGrid =
            "RFJBQ08CAgEAAADCA4ACAQAAAQAQAAEAEQAQAAEAAgARAAIAEgARAAIAAwASAAMAEwASAAMABAATAAQAFAATAAQABQAUAAUAFQAUAAUABgAVAAYA" +
            "FgAVAAYABwAWAAcAFwAWAAcACAAXAAgAGAAXAAgACQAYAAkAGQAYAAkACgAZAAoAGgAZAAoACwAaAAsAGwAaAAsADAAbAAwAHAAbAAwADQAcAA0A" +
            "HQAcAA0ADgAdAA4AHgAdAA4ADwAeAA8AHwAeABAAEQAgABEAIQAgABEAEgAhABIAIgAhABIAEwAiABMAIwAiABMAFAAjABQAJAAjABQAFQAkABUA" +
            "JQAkABUAFgAlABYAJgAlABYAFwAmABcAJwAmABcAGAAnABgAKAAnABgAGQAoABkAKQAoABkAGgApABoAKgApABoAGwAqABsAKwAqABsAHAArABwA" +
            "LAArABwAHQAsAB0ALQAsAB0AHgAtAB4ALgAtAB4AHwAuAB8ALwAuACAAIQAwACEAMQAwACEAIgAxACIAMgAxACIAIwAyACMAMwAyACMAJAAzACQA" +
            "NAAzACQAJQA0ACUANQA0ACUAJgA1ACYANgA1ACYAJwA2ACcANwA2ACcAKAA3ACgAOAA3ACgAKQA4ACkAOQA4ACkAKgA5ACoAOgA5ACoAKwA6ACsA" +
            "OwA6ACsALAA7ACwAPAA7ACwALQA8AC0APQA8AC0ALgA9AC4APgA9AC4ALwA+AC8APwA+ADAAMQBAADEAQQBAADEAMgBBADIAQgBBADIAMwBCADMA" +
            "QwBCADMANABDADQARABDADQANQBEADUARQBEADUANgBFADYARgBFADYANwBGADcARwBGADcAOABHADgASABHADgAOQBIADkASQBIADkAOgBJADoA" +
            "SgBJADoAOwBKADsASwBKADsAPABLADwATABLADwAPQBMAD0ATQBMAD0APgBNAD4ATgBNAD4APwBOAD8ATwBOAEAAQQBQAEEAUQBQAEEAQgBRAEIA" +
            "UgBRAEIAQwBSAEMAUwBSAEMARABTAEQAVABTAEQARQBUAEUAVQBUAEUARgBVAEYAVgBVAEYARwBWAEcAVwBWAEcASABXAEgAWABXAEgASQBYAEkA" +
            "WQBYAEkASgBZAEoAWgBZAEoASwBaAEsAWwBaAEsATABbAEwAXABbAEwATQBcAE0AXQBcAE0ATgBdAE4AXgBdAE4ATwBeAE8AXwBeAFAAUQBgAFEA" +
            "YQBgAFEAUgBhAFIAYgBhAFIAUwBiAFMAYwBiAFMAVABjAFQAZABjAFQAVQBkAFUAZQBkAFUAVgBlAFYAZgBlAFYAVwBmAFcAZwBmAFcAWABnAFgA" +
            "aABnAFgAWQBoAFkAaQBoAFkAWgBpAFoAagBpAFoAWwBqAFsAawBqAFsAXABrAFwAbABrAFwAXQBsAF0AbQBsAF0AXgBtAF4AbgBtAF4AXwBuAF8A" +
            "bwBuAGAAYQBwAGEAcQBwAGEAYgBxAGIAcgBxAGIAYwByAGMAcwByAGMAZABzAGQAdABzAGQAZQB0AGUAdQB0AGUAZgB1AGYAdgB1AGYAZwB2AGcA" +
            "dwB2AGcAaAB3AGgAeAB3AGgAaQB4AGkAeQB4AGkAagB5AGoAegB5AGoAawB6AGsAewB6AGsAbAB7AGwAfAB7AGwAbQB8AG0AfQB8AG0AbgB9AG4A" +
            "fgB9AG4AbwB+AG8AfwB+AHAAcQCAAHEAgQCAAHEAcgCBAHIAggCBAHIAcwCCAHMAgwCCAHMAdACDAHQAhACDAHQAdQCEAHUAhQCEAHUAdgCFAHYA" +
            "hgCFAHYAdwCGAHcAhwCGAHcAeACHAHgAiACHAHgAeQCIAHkAiQCIAHkAegCJAHoAigCJAHoAewCKAHsAiwCKAHsAfACLAHwAjACLAHwAfQCMAH0A" +
            "jQCMAH0AfgCNAH4AjgCNAH4AfwCOAH8AjwCOAIAAgQCQAIEAkQCQAIEAggCRAIIAkgCRAIIAgwCSAIMAkwCSAIMAhACTAIQAlACTAIQAhQCUAIUA" +
            "lQCUAIUAhgCVAIYAlgCVAIYAhwCWAIcAlwCWAIcAiACXAIgAmACXAIgAiQCYAIkAmQCYAIkAigCZAIoAmgCZAIoAiwCaAIsAmwCaAIsAjACbAIwA" +
            "nACbAIwAjQCcAI0AnQCcAI0AjgCdAI4AngCdAI4AjwCeAI8AnwCeAJAAkQCgAJEAoQCgAJEAkgChAJIAogChAJIAkwCiAJMAowCiAJMAlACjAJQA" +
            "pACjAJQAlQCkAJUApQCkAJUAlgClAJYApgClAJYAlwCmAJcApwCmAJcAmACnAJgAqACnAJgAmQCoAJkAqQCoAJkAmgCpAJoAqgCpAJoAmwCqAJsA" +
            "qwCqAJsAnACrAJwArACrAJwAnQCsAJ0ArQCsAJ0AngCtAJ4ArgCtAJ4AnwCuAJ8ArwCuAKAAoQCwAKEAsQCwAKEAogCxAKIAsgCxAKIAowCyAKMA" +
            "swCyAKMApACzAKQAtACzAKQApQC0AKUAtQC0AKUApgC1AKYAtgC1AKYApwC2AKcAtwC2AKcAqAC3AKgAuAC3AKgAqQC4AKkAuQC4AKkAqgC5AKoA" +
            "ugC5AKoAqwC6AKsAuwC6AKsArAC7AKwAvAC7AKwArQC8AK0AvQC8AK0ArgC9AK4AvgC9AK4ArwC+AK8AvwC+ALAAsQDAALEAwQDAALEAsgDBALIA" +
            "wgDBALIAswDCALMAwwDCALMAtADDALQAxADDALQAtQDEALUAxQDEALUAtgDFALYAxgDFALYAtwDGALcAxwDGALcAuADHALgAyADHALgAuQDIALkA" +
            "yQDIALkAugDJALoAygDJALoAuwDKALsAywDKALsAvADLALwAzADLALwAvQDMAL0AzQDMAL0AvgDNAL4AzgDNAL4AvwDOAL8AzwDOAMAAwQDQAMEA" +
            "0QDQAMEAwgDRAMIA0gDRAMIAwwDSAMMA0wDSAMMAxADTAMQA1ADTAMQAxQDUAMUA1QDUAMUAxgDVAMYA1gDVAMYAxwDWAMcA1wDWAMcAyADXAMgA" +
            "2ADXAMgAyQDYAMkA2QDYAMkAygDZAMoA2gDZAMoAywDaAMsA2wDaAMsAzADbAMwA3ADbAMwAzQDcAM0A3QDcAM0AzgDdAM4A3gDdAM4AzwDeAM8A" +
            "3wDeANAA0QDgANEA4QDgANEA0gDhANIA4gDhANIA0wDiANMA4wDiANMA1ADjANQA5ADjANQA1QDkANUA5QDkANUA1gDlANYA5gDlANYA1wDmANcA" +
            "5wDmANcA2ADnANgA6ADnANgA2QDoANkA6QDoANkA2gDpANoA6gDpANoA2wDqANsA6wDqANsA3ADrANwA7ADrANwA3QDsAN0A7QDsAN0A3gDtAN4A" +
            "7gDtAN4A3wDuAN8A7wDuAOAA4QDwAOEA8QDwAOEA4gDxAOIA8gDxAOIA4wDyAOMA8wDyAOMA5ADzAOQA9ADzAOQA5QD0AOUA9QD0AOUA5gD1AOYA" +
            "9gD1AOYA5wD2AOcA9wD2AOcA6AD3AOgA+AD3AOgA6QD4AOkA+QD4AOkA6gD5AOoA+gD5AOoA6wD6AOsA+wD6AOsA7AD7AOwA/AD7AOwA7QD8AO0A" +
            "/QD8AO0A7gD9AO4A/gD9AO4A7wD+AO8A/wD+AAECAAkDAAADCQIAAQICAAEBABADQCsBAQEuwRAh0NBustfdeITdCOarma6MuG5cjmCsK0FWfreh" +
            "xgH13gtwEBFZ9wEAIqICOwBARGRGBACIiC8ZACAikisAgIgIeQEAImK3DwCIiFQWACAi8nIAgIiIrwMAImIhBQCIiFavAAARkdsbACAi1mEAgIh4" +
            "MQMAAsDV6LSJiCSYAAARsf8CAETEewcAEBHLMQBAROSzBACIiKMJACAiBogAgIgIKwAAImIOAACIiAqJAAAREYoTACAiQgoAgIg4ygEAIiJIDwCI" +
            "iMaKAAABYFp12kREMgMAEBFFTwBARKwfAAARkasUACAiBtwAgIiIQwAAIuITAACIiCIBACAijgMAgIgYoAEAIiK9JABARExxAAAR0foUACAiIlkC" +
            "AETEngUAEAAmbKhNRFSkAAARMRsCAERE2lIAgIi4bwIAIqLaCQCIiM8dACAiamsCAEREzGIAgIioaA0AEBEZPgBARNTwAQARcdwBAEREHUQAgIi4" +
            "mAAAIiLvBAAIAEDgtEVE1FMAgIhYcgIAIqJtIgBARHRPAAARkbsCAEREhmAAgIjoUQkAEBEDdABARAQ9BACIiJQKACAiSlcAgIhY5wAAIqK5KQBA" +
            "RJS1AQARcS8BAASAItRpExExGwBARDwwAAARkdkXACAiupsCAETEwAAAEBHpDAEAIiKUCACIiHUjACAiuqUAgIg44gAAIuK9CwCIiMA1ACAiJtgA" +
            "gIi4wQEAIqJ3DAAIAJin0yYi5lYAgIjoTw0AEBGTAwBARGTTBgCIiCrJAAAREfgCAETE/QcAEBHRHwEAImKfCACIiPQrACAiejkAgIhYPwEAIqIq" +
            "BgCIiNc0ACAi0rsAgACgLOy0ERHzAwBARCTMBgCIiIcxACAiOogAgIgYhAMAIiKJDACIiCa8AAARkbEaACAiejQAgIjocg0AEBFZowEAIqKkNwBA" +
            "RDTUAQAR0XUGAETESBcAEACXH2qLiGiuAAAR0UkaACAi8g8AgIi41AEAIqK2AQCIiGSNAAARkeYEAETExxUAEBFdBAEAIiKFJABARDyeAQARkXcE" +
            "AETEPxkAEBGJGABAROTcAQABgJd02kREn1YAgIiIGwEAIiI2AwCIiN82ACAighACAETEZRIAEBENOQBARHyPAAARcSABAERE6B0AEBHlBQEAIiJK" +
            "JABAREQMAAAREZQCAEREJ1MAgACAAjttIiJ8AQCIiIYeACAibmIAgIgoTgkAEBE3AQBARCQ4BACIiM0qACAiopQAgIjoswsAEBFdVABARAREBACI" +
            "iGkaACAi8lsAgIhoSgkAEBFxIgEAAiBkOG0R0WcaACAi2m8AgIgIyAgAEBERHABAREStAAARcdUHAERE7QcAEBHlXABARKyuAQARcYsBAETE3wkA" +
            "EBGFfgBARIzZAAAREQ0DAEREPQUAEADmbKdNRBzxAAAREbcEAEREVR8AEBHlqgEAIiJuAgCIiN0CACAi1nMAgIioCAMAImK7BwCIiOEEACAi8rsC" +
            "AEREtWMAgIgI6wgAEBGbdgBARJT6AQAB4JOF2kRE2xkAEBG9UQBARHzyAQARMdgBAEREkgUAEBFHQABARByHAAARUYIFAETE+RYAEBElEQBARCxq" +
            "AAARkYcCAETEcQAAEBFhdgBARKRzAQABwHjQaSMi2gMAgIgIXAMAIuKCBQCIiMURACAixkcAgIhIuwAAIuLxDQCIiCwkACAitlAAgIgo5QEAIqIc" +
            "DwCIiB70AAARkQUBAETEqQMAEBH5XQEAAqAsOW0RUY4RACAiWlkAgIjY8wAAIqITDACIiHYhACAilkkAgIgogAkAEBGzCgBARAQ3BACIiIkyACAi" +
            "4vcAgIhIPAAAIuLgCwCIiJ4gACAiyugAAAAAAAD//wAAAAEBAQGjBEEeA+EB//////////974R9N+MWkRp7hCbOkZkNc8KEKejYIhSIKs6RmQ1zw" +
            "oQp6NgiFIgqzpGZDXPChCno2CIUiCrOkZkNc8KEKejYIhSIKs6RmQ1zwoQp6NgitcIAAAAAA/w8AAAAAAAAAAAC/AAAAAAAAcEAQAAAAAAAAAAAA" +
            "AIA/DA==";

        public static Vec3 DracoGridPoint(int i, int j)
        {
            double s = Math.Sin(i * 12.9898 + j * 78.233) * 43758.5453;
            return new Vec3(i * 0.25f, (float)(s - Math.Floor(s)) - 0.5f, j * 0.2f);
        }

        /// <summary>A mesh tile like Matterport's: a GLB whose one primitive is <see cref="DracoGrid"/>, textured with a
        /// KTX2 image (Matterport serves a JPEG of each too).</summary>
        public static byte[] TileGlb(string image)
        {
            var draco = Convert.FromBase64String(DracoGrid);
            var json = "{\"asset\":{\"version\":\"2.0\"},\"extensionsUsed\":[\"KHR_draco_mesh_compression\",\"KHR_texture_basisu\"]," +
                       $"\"buffers\":[{{\"byteLength\":{draco.Length}}}],\"bufferViews\":[{{\"buffer\":0,\"byteLength\":{draco.Length}}}]," +
                       "\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0,\"TEXCOORD_0\":1},\"material\":0,\"extensions\":" +
                       "{\"KHR_draco_mesh_compression\":{\"bufferView\":0,\"attributes\":{\"POSITION\":0,\"TEXCOORD_0\":1}}}}]}]," +
                       "\"materials\":[{\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0}}}]," +
                       $"\"textures\":[{{\"extensions\":{{\"KHR_texture_basisu\":{{\"source\":0}}}}}}],\"images\":[{{\"uri\":\"{image}\"}}]}}";
            var jsonBytes = Encoding.UTF8.GetBytes(json.PadRight((json.Length + 3) / 4 * 4));
            var bin = new byte[(draco.Length + 3) / 4 * 4];
            Array.Copy(draco, bin, draco.Length);
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(0x46546C67u); // "glTF"
                w.Write(2);
                w.Write(12 + 8 + jsonBytes.Length + 8 + bin.Length);
                w.Write(jsonBytes.Length);
                w.Write(0x4E4F534Au); // "JSON"
                w.Write(jsonBytes);
                w.Write(bin.Length);
                w.Write(0x004E4942u); // "BIN"
                w.Write(bin);
                w.Flush();
                return ms.ToArray();
            }
        }

        /// <summary>A tileset with REPLACE refinement whose one leaf is <paramref name="leaf"/>.</summary>
        public static string Tileset(string leaf) =>
            "{\"asset\":{\"version\":\"1.0\"},\"geometricError\":10,\"root\":{\"refine\":\"REPLACE\",\"geometricError\":5," +
            "\"content\":{\"uri\":\"0/lod0_000.glb\"},\"children\":[{\"geometricError\":0,\"content\":{\"uri\":\"" + leaf + "\"}}]}}";

        /// <summary>Two triangles, oriented so their normal points towards <paramref name="toward"/>.</summary>
        static void Quad(List<Vec3> positions, List<int> indices, Vec3 a, Vec3 b, Vec3 c, Vec3 d, Vec3 toward)
        {
            int i = positions.Count;
            positions.AddRange(new[] { a, b, c, d });
            bool flip = Vec3.Dot(Vec3.Cross(b - a, c - a), toward - a) < 0;
            indices.AddRange(flip ? new[] { i, i + 2, i + 1, i, i + 3, i + 2 } : new[] { i, i + 1, i + 2, i, i + 2, i + 3 });
        }

        /// <summary>A closed box with faces pointing out.</summary>
        static void Box(List<Vec3> positions, List<int> indices, Vec3 min, Vec3 max)
        {
            var c = (min + max) * 0.5f;
            Vec3 V(int i) => new Vec3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
            foreach (var f in new[] { new[] { 0, 1, 3, 2 }, new[] { 4, 5, 7, 6 }, new[] { 0, 1, 5, 4 }, new[] { 2, 3, 7, 6 }, new[] { 0, 2, 6, 4 }, new[] { 1, 3, 7, 5 } })
            {
                var faceCentre = (V(f[0]) + V(f[2])) * 0.5f;
                Quad(positions, indices, V(f[0]), V(f[1]), V(f[2]), V(f[3]), faceCentre * 2f - c);
            }
        }

        static SpaceGeometry Build(List<Vec3> positions, List<int> indices)
        {
            var pos = positions.ToArray();
            var idx = indices.ToArray();
            var min = pos[0];
            var max = pos[0];
            foreach (var p in pos) { min = Vec3.Min(min, p); max = Vec3.Max(max, p); }
            var geo = new SpaceGeometry { Positions = pos, Bvh = new Bvh(pos, idx), BoundsMin = min, BoundsMax = max };
            geo.Parts.Add(new GeometryPart { Material = "m", Indices = idx });
            return geo;
        }

        /// <summary>The room with three sweeps (1.5 m camera height, two panorama tiers each), neighbours in a chain.</summary>
        public static SpaceModel RoomModel()
        {
            var geo = Room();
            var sweeps = new List<SweepInfo>();
            var spots = new[] { new Vec3(1, 1.5f, 1), new Vec3(3, 1.5f, 1), new Vec3(3, 1.5f, 2.2f) };
            for (int i = 0; i < spots.Length; i++)
            {
                var entry = new SweepEntry { Id = "s" + i, Index = i };
                entry.Tiers.Add(new PanoTier { Size = 512, Directory = $"panos/s{i}/512", Extension = "jpg" });
                entry.Tiers.Add(new PanoTier { Size = 2048, Directory = $"panos/s{i}/2048", Extension = "jpg" });
                sweeps.Add(new SweepInfo { Index = i, Id = entry.Id, Position = spots[i], FloorPoint = new Vec3(spots[i].X, 0.01f, spots[i].Z), FloorHit = true, WorldFromPano = Mat3.Identity, Entry = entry });
            }
            sweeps[0].Neighbors.Add(1);
            sweeps[1].Neighbors.AddRange(new[] { 0, 2 });
            sweeps[2].Neighbors.Add(1);
            return new SpaceModel(new SpaceManifest { ModelId = "room", Name = "Room" }, new SpaceCalibration(), geo, sweeps);
        }
    }
}
