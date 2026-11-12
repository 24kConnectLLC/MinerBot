using DataAccessLibrary.Armor.Models;

namespace DataAccessLibrary.Armor
{
    public interface IArmorSubmissionData
    {
        Task<bool> ChangeStatus(long id, string? expectedStatus, string newStatus, DataTransaction? transaction = null);
        Task<ArmorSubmission?> GetSubmission(long id, DataTransaction? transaction = null);
        Task<bool> IsApprovedNameTaken(string name, long exceptId, DataTransaction? transaction = null);
        Task<Page<ArmorSubmission>> ListByStatus(string? status, int page, int pageSize);
        Task<T> RunInTransaction<T>(Func<DataTransaction, Task<T>> work);
        Task<Page<ArmorSubmission>> Search(string text, string? status, int page, int pageSize);
    }
}
