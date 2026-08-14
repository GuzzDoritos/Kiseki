using Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Core
{
    public class ImmersionDbContext : DbContext
    {
        public DbSet<MediaWork> MediaWorks {  get; set; }

        public DbSet<ImmersionLog> ImmersionLogs { get; set; }

        public string DbPath { get; }

        public ImmersionDbContext()
        {
            var folder = Environment.SpecialFolder.LocalApplicationData;
            var path = Environment.GetFolderPath(folder);
            DbPath = Path.Join(path, "immersiontracker.db");
        }

        protected override void OnConfiguring(DbContextOptionsBuilder options)
        {
            //this shouldnt be here but im learning
            options.UseSqlite($"Data Source={DbPath}");
        }
    }
}
