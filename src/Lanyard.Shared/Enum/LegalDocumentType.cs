namespace Lanyard.Shared.Enum;

/// <summary>
/// The customer-facing legal documents a company publishes on its ordering site.
///
/// Stored as separate documents rather than one, because they are edited and reviewed at
/// different times: a venue might change its collection window and refund wording without
/// touching its privacy policy. Terms and refunds still render on one page, which is what a
/// customer sees today.
/// </summary>
public enum LegalDocumentType
{
    OrderingTerms = 0,
    RefundPolicy = 1,
    PrivacyPolicy = 2
}
