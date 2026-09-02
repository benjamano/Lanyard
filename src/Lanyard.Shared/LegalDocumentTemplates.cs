using System.Text.RegularExpressions;
using Lanyard.Shared.Enum;

namespace Lanyard.Shared;

/// <summary>
/// Lanyard's default wording for the customer-facing legal documents.
///
/// A company that has never opened the editor sees these as its starting point. They are a
/// draft, not a finished document: the parts only the venue can know are left as square-bracket
/// prompts, and a document cannot be published to customers until someone has replaced them.
///
/// Deliberately plain text rather than a template with substitution tokens. Company details used
/// to live in separate fields and be injected into the wording, which meant the same address was
/// entered in one place and read in another, and neither read as the sentence it ended up in.
/// Staff now write the whole document, which is what customers actually read.
/// </summary>
public static class LegalDocumentTemplates
{
    public static string DisplayName(this LegalDocumentType type) => type switch
    {
        LegalDocumentType.OrderingTerms => "Ordering terms",
        LegalDocumentType.RefundPolicy => "Refund policy",
        LegalDocumentType.PrivacyPolicy => "Privacy policy",
        _ => type.ToString()
    };

    /// <summary>
    /// True while the wording still contains something a customer must never read: a
    /// square-bracket prompt from the current drafts, or a {{Token}} left over from when these
    /// documents were assembled from company fields.
    ///
    /// The second case is not hypothetical. Documents saved before substitution was removed still
    /// contain those tokens, and with nothing left to substitute them they would render literally
    /// on a checkout page as "{{ContactEmail}}".
    /// </summary>
    public static bool HasUnfilledPrompt(string body) =>
        Regex.IsMatch(body, @"\[[^\]]{3,60}\]") || Regex.IsMatch(body, @"\{\{\s*\w+\s*\}\}");

    public static string Default(LegalDocumentType type) => type switch
    {
        LegalDocumentType.OrderingTerms => OrderingTerms,
        LegalDocumentType.RefundPolicy => RefundPolicy,
        LegalDocumentType.PrivacyPolicy => PrivacyPolicy,
        _ => string.Empty
    };

    private const string OrderingTerms = """
        <h1>Ordering food</h1>
        <p>These terms apply when you order food using the QR code at your table. Your contract is with <strong>[your registered trading name]</strong>, company number [your company number], registered at [your registered address].</p>
        <h2>Prices and payment</h2>
        <p>Prices are shown on the menu and include VAT. The total you see before you pay is the total you pay. We don't add service or booking fees. Payment is taken when you place your order and is handled by Stripe; we never see or store your card details.</p>
        <h2>When we start cooking</h2>
        <p>We begin preparing your order as soon as your payment is confirmed. Your order isn't accepted, and nothing is charged, until that happens.</p>
        <h2>Allergies and intolerances</h2>
        <p>Each dish lists the allergens it contains and any it may contain through cross-contamination. If you have an allergy or intolerance, please also speak to a member of staff before ordering. They can tell you how a dish is prepared and whether we can make it safely for you. Please don't rely on the notes box alone.</p>
        <h2>Collection</h2>
        <p>Orders are for collection at the counter. We'll hold your order for [number] minutes after it's ready, after which we may dispose of it for food safety reasons and won't be able to refund it.</p>
        <h2>Changing your mind</h2>
        <p>Because food is prepared fresh to order, the 14-day right to cancel under the Consumer Contracts Regulations 2013 does not apply. If you need to change or cancel, speak to staff straight away. If we haven't started your order we'll usually be able to help.</p>
        """;

    private const string RefundPolicy = """
        <h1>Refunds</h1>
        <p><strong>We'll refund you in full if:</strong> we cancel your order, we can't make something you've paid for, or your order isn't of satisfactory quality and you tell staff before you leave.</p>
        <p><strong>We may not refund if:</strong> you change your mind after we've started preparing your order, or you don't collect it within [number] minutes of it being ready.</p>
        <p><strong>How refunds work.</strong> Refunds are made in full to the card you paid with. We can't refund to a different card or in cash. Your bank usually takes 5-10 working days to show it.</p>
        <p><strong>Part of an order.</strong> If only part of your order was wrong, speak to a member of staff at the venue.</p>
        <h2>Contact</h2>
        <p>[your contact email] [your contact phone number]</p>
        """;

    private const string PrivacyPolicy = """
        <h1>Privacy policy</h1>
        <p>This site is operated by <strong>[your registered trading name]</strong>, company number [your company number], registered at [your registered address]. We are the data controller for the information described below. The site itself is built and hosted for us by Lanyard, which processes that information only on our instructions.</p>
        <h2>What we collect when you order food</h2>
        <p>Ordering is anonymous. We don't ask for your name, and you don't create an account. When you place an order we record the table you scanned, what you ordered and the price, the allergens declared for those dishes, and anything you type in the notes box. We also keep a reference to the payment so it can be matched up or refunded.</p>
        <p>The notes box is free text and people often use it to tell us about an allergy or intolerance. That is information about your health, so we keep it only as long as it is useful: <strong>we automatically delete the contents of the notes box 30 days after the order</strong>. The rest of the order is kept as a sales and food-safety record.</p>
        <h2>Card payments</h2>
        <p>Card payments are handled by Stripe. Your card details are entered directly into Stripe's own payment form and never reach this site or our systems; we only ever see a reference to the payment, the amount and whether it succeeded. Stripe processes that data as a controller in its own right under its own privacy policy, and is required to keep transaction records to meet its legal obligations.</p>
        <h2>Why we're allowed to use it</h2>
        <p>We use your order to perform the contract you entered into when you bought food. We keep sales records afterwards to comply with our legal obligations, mainly tax and accounting. Where you tell us about an allergy, we use that to protect your vital interests and to meet food-safety obligations.</p>
        <p>We do not use any of this for marketing, we do not build a profile of you, and we do not sell or share it with anyone except the payment and hosting providers above.</p>
        <h2>How long we keep it</h2>
        <ul>
        <li>Notes box: deleted automatically 30 days after the order.</li>
        <li>The order itself (items, prices, allergens, payment reference): kept for six years, which is the period we're required to retain financial records for.</li>
        </ul>
        <h2>Cookies</h2>
        <p>We use only strictly-necessary cookies and equivalent browser storage: enough to keep the site working and secure, and to remember your basket while you're ordering so that closing the page doesn't lose it. There is no advertising or analytics tracking on this site, so there is nothing here to consent to or opt out of.</p>
        <h2>Your rights</h2>
        <p>You have the right to ask for a copy of the information we hold about you, to have it corrected or deleted, and to object to or restrict how we use it. Because ordering is anonymous we usually cannot link an order to you by name, so please quote your order reference or the card receipt when you get in touch, or we may not be able to find it.</p>
        <p>If you're unhappy with how we've handled your information you can complain to the Information Commissioner's Office at ico.org.uk.</p>
        <h2>Contact</h2>
        <p>[your contact email] [your contact phone number]</p>
        """;
}
