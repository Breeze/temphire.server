using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TempHire.Model;

/// <summary>Ported from <c>DomainModel/PhoneNumber.cs</c>.</summary>
public class PhoneNumber : AuditEntityBase, IHasRoot
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public Guid Id { get; set; }

    /// <summary>
    /// The whole U.S. number, area code included, stored as the user typed it once it passes the
    /// format rule (<c>PHONE_NUMBER_FORMAT</c> in the validation metadata); the clients rewrite a
    /// valid one as <c>(206) 555-1234</c> when the user leaves the field.  The original kept the
    /// area code in a field of its own.
    /// </summary>
    [Required]
    [StringLength(20)]
    public string Number { get; set; }

    public Guid PhoneNumberTypeId { get; set; }

    public Guid StaffingResourceId { get; set; }

    public bool Primary { get; set; }

    public StaffingResource StaffingResource { get; set; }

    public PhoneNumberType PhoneNumberType { get; set; }

    [NotMapped]
    public object Root => StaffingResource;
}
