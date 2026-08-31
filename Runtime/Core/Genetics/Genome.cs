// Генетика (порт genome.rs): DNA→NT карта, канальные гены, FlyGenome.

using System.Collections.Generic;

namespace Prometheus.Runtime.Genetics
{
    public class DnaGene
    {
        public string Gene;
        public string Neurotransmitter;
        public sbyte Sign;
    }

    public class ChannelGene
    {
        public string Gene;
        public string Family;
        public string Param;
    }

    public static class GenomeParser
    {
        public static List<DnaGene> ParseDnaMap(string csv)
        {
            var outList = new List<DnaGene>();
            bool first = true;
            foreach (var raw in csv.Split('\n'))
            {
                var line = raw.Trim();
                if (first) { first = false; continue; }
                if (line.Length == 0) continue;
                var f = line.Split(',');
                if (f.Length < 4) continue;
                sbyte sign;
                sbyte.TryParse(f[3].Trim(), out sign);
                outList.Add(new DnaGene
                {
                    Gene = f[0].Trim(),
                    Neurotransmitter = f[1].Trim(),
                    Sign = sign,
                });
            }
            return outList;
        }

        public static List<ChannelGene> ParseChannelMap(string csv)
        {
            var outList = new List<ChannelGene>();
            bool first = true;
            foreach (var raw in csv.Split('\n'))
            {
                var line = raw.Trim();
                if (first) { first = false; continue; }
                if (line.Length == 0) continue;
                var f = SplitCsv(line);
                if (f.Count < 3) continue;
                outList.Add(new ChannelGene
                {
                    Gene = f[0].Trim(),
                    Family = f[1].Trim(),
                    Param = f[2].Trim(),
                });
            }
            return outList;
        }

        static List<string> SplitCsv(string line)
        {
            var fields = new List<string>();
            var cur = new System.Text.StringBuilder();
            bool inQuotes = false;
            foreach (var ch in line)
            {
                if (ch == '"') inQuotes = !inQuotes;
                else if (ch == ',' && !inQuotes) fields.Add(cur.ToString());
                else cur.Append(ch);
            }
            fields.Add(cur.ToString());
            return fields;
        }
    }

    public class FlyGenome
    {
        public string NtGene;
        public string MorphogenGene;
        public string GuidanceGene;
        public Dictionary<string, string> Sequences = new Dictionary<string, string>();
        public string AdhesionGene;
        public List<string> ChannelGenes = new List<string>();

        public static FlyGenome HalfCenter() => new FlyGenome
        {
            NtGene = "Gad1",
            MorphogenGene = "dpp",
            GuidanceGene = "fra",
            AdhesionGene = "Dscam1",
            ChannelGenes = { "Ih", "KCNQ" },
        };

        public Prometheus.Runtime.CellType? CellType(List<DnaGene> map)
        {
            var g = map.Find(x => x.Gene == NtGene);
            if (g == null) return null;
            return g.Sign < 0 ? Prometheus.Runtime.CellType.Inhibitory : Prometheus.Runtime.CellType.Excitatory;
        }

        public string Neurotransmitter(List<DnaGene> map)
        {
            var g = map.Find(x => x.Gene == NtGene);
            return g?.Neurotransmitter;
        }

        public Recognition Recognition(List<ChannelGene> channelMap)
        {
            bool has = AdhesionGene != null &&
                channelMap.Exists(c => c.Gene == AdhesionGene && c.Param == "cell-recognition");
            return has ? Prometheus.Runtime.Recognition.SameType()
                        : Prometheus.Runtime.Recognition.All();
        }

        public List<ChannelGene> Channels(List<ChannelGene> channelMap) =>
            channelMap.FindAll(c => ChannelGenes.Contains(c.Gene));
    }
}
