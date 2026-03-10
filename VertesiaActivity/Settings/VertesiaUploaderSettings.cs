using System.ComponentModel.DataAnnotations;
using STG.Common.DTO;
using STG.Common.DTO.Metadata;
using STG.RT.API.Activity;
using VertesiaActivity.Properties;

namespace VertesiaActivity.Settings
{
    public class VertesiaUploaderSettings : ActivityConfigBase<VertesiaUploaderSettings>
    {
        public VertesiaUploaderSettings()
        {
            ResultMapping = new SerializableDictionary<string, string>();
            TableMapping = new SerializableDictionary<string, string>();
        }

        [Required]
        [Display(
            Name = nameof(Resources.VertesiaUploaderSettings_ApiKey_Name),
            Description = nameof(Resources.VertesiaUploaderSettings_ApiKey_Description),
            Order = 1,
            ResourceType = typeof(Resources))]
        [InputType(InputType.password)]
        public string ApiKey { get; set; }

        [Required]
        [Display(
            Name = nameof(Resources.VertesiaUploaderSettings_ApiUrl_Name),
            Description = nameof(Resources.VertesiaUploaderSettings_ApiUrl_Description),
            Order = 2,
            ResourceType = typeof(Resources))]
        [InputType(InputType.text)]
        public string ApiUrl { get; set; }

        [Required]
        [Display(
            Name = nameof(Resources.VertesiaUploaderSettings_ContentType_Name),
            Description = nameof(Resources.VertesiaUploaderSettings_ContentType_Description),
            Order = 3,
            ResourceType = typeof(Resources))]
        [InputType(InputType.text)]
        public string ContentType { get; set; }

        [Required]
        [Display(
            Name = nameof(Resources.VertesiaUploaderSettings_InteractionId_Name),
            Description = nameof(Resources.VertesiaUploaderSettings_InteractionId_Description),
            Order = 4,
            ResourceType = typeof(Resources))]
        [InputType(InputType.text)]
        public string InteractionId { get; set; }

        /// <summary>
        /// Maps flat fields from the interaction result JSON to index fields on the child document.
        /// Key   = flattened result key (e.g. "invoice_number", "vendor.name")
        /// Value = "DocumentType.FieldName" (e.g. "Invoice.InvoiceNumber")
        /// </summary>
        [Display(
            Name = nameof(Resources.VertesiaUploaderSettings_ResultMapping_Name),
            Description = nameof(Resources.VertesiaUploaderSettings_ResultMapping_Description),
            Order = 5,
            ResourceType = typeof(Resources))]
        [InputType(InputType.dictionary)]
        public SerializableDictionary<string, string> ResultMapping { get; set; }

        /// <summary>
        /// Maps array fields from the interaction result JSON to tables on the child document.
        /// Key   = "array_prefix.field_name" (e.g. "line_items.description")
        /// Value = "TableName.ColumnName"    (e.g. "LineItems.Description")
        /// All entries sharing the same array prefix produce one table with one row per element.
        /// </summary>
        [Display(
            Name = nameof(Resources.VertesiaUploaderSettings_TableMapping_Name),
            Description = nameof(Resources.VertesiaUploaderSettings_TableMapping_Description),
            Order = 6,
            ResourceType = typeof(Resources))]
        [InputType(InputType.dictionary)]
        public SerializableDictionary<string, string> TableMapping { get; set; }
    }
}
