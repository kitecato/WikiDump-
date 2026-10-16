using System; using System.Collections.Generic; using WikiDumpMods;
class O { static void Main() { var r = DataIndex.ExactOneOfEach(new List<double>{5,3,1,1}, 2); Console.WriteLine(string.Join(" ", Array.ConvertAll<double,string>(r, x => x.ToString("F5")))); } }
