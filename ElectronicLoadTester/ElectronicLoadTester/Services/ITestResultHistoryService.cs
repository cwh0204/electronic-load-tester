using System.Collections.Generic;
using System.Threading.Tasks;
using ElectronicLoadTester.Models;

namespace ElectronicLoadTester.Services
{
    public interface ITestResultHistoryService
    {
        Task InitializeDatabaseAsync();
        Task<string> GenerateNextSerialNumberAsync(string targetSystem, string railName);
        Task<long> SaveTestResultAsync(TestMasterEntity master, IEnumerable<TestStepDetailEntity> steps);
        Task<IEnumerable<TestMasterEntity>> SearchHistoryAsync(TestHistoryFilter filter);
        Task<TestMasterEntity?> GetMasterWithDetailsByIdAsync(long masterId);
        Task<bool> DeleteTestResultAsync(long masterId);
    }
}