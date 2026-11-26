namespace DataAccessLibrary.Armor.Models
{
    // An armor waiting for a review, as the new-submission alerts read it: its ID and when the website saved it, by the
    // website's clock, and nothing its submitter wrote. A long, as the table's ID is a BIGINT, unlike ArmorStyle's int.
    public class PendingSubmission
    {
        public long ID { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
