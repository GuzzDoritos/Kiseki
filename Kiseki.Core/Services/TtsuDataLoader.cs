using Kiseki.Core.DTOs;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace Kiseki.Core.Services
{
    public class TtsuDataLoader
    {
        static string root = @"C:\ttu-reader-data";


        static public async Task<List<TtsuBookContainer>> LoadTtsuData()
        {
            List<TtsuBookContainer> books = new List<TtsuBookContainer>();
            var dirs = from dir in
                           Directory.EnumerateDirectories(root)
                       select dir;

            foreach (var dir in dirs)
            {
                string dirName = dir.Replace(Path.GetDirectoryName(dir) + Path.DirectorySeparatorChar, "");
                var b = new TtsuBookContainer { Title = dirName };

                DirectoryInfo searchDir = new DirectoryInfo(dir);
                var statisticsFile = searchDir.GetFiles("statistics*").FirstOrDefault().FullName;

                var ttsuEntries = JsonSerializer.Deserialize<List<TtsuReaderDTO>>(File.ReadAllText(statisticsFile));

                b.Entries = ttsuEntries;

                books.Add(b);
            }

            return books;       
        }
    }
}
