using ƎPIDGRAPH.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ƎPIDGRAPH.Services
{
    public interface IBBLFileService
    {
        Task<List<LogFile>> LoadMultipleAsync(IEnumerable<string> bblPaths);
    }
}