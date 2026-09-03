using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PublishLegalDocumentsAndDropCompanyLegalFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Added first, because the data migration below writes to it.
            migrationBuilder.AddColumn<bool>(
                name: "IsPublished",
                table: "CompanyLegalDocuments",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Carry the old fields forward into real documents before dropping them.
            //
            // Two reasons this is not simply a drop. A company that had filled these in has done
            // the work already and should not be asked to retype it. And publishing is now what
            // permits a venue to take orders, so without this every company that could sell
            // yesterday would silently stop today until somebody noticed and published three
            // documents.
            //
            // Only companies that had actually completed their details are migrated. A company
            // with blanks was already barred from ordering, so it gains nothing from being
            // handed a published document full of gaps.
            migrationBuilder.Sql(@"
                INSERT INTO ""CompanyLegalDocuments""
                    (""CompanyId"", ""DocumentType"", ""BodyHtml"", ""IsPublished"", ""CreateDate"", ""UpdateDate"")
                SELECT c.""Id"", d.doc_type, d.body, true, now() at time zone 'utc', now() at time zone 'utc'
                FROM ""Companies"" c
                CROSS JOIN LATERAL (VALUES
                    (0,
                     '<h1>Ordering food</h1>' ||
                     '<p>These terms apply when you order food using the QR code at your table. Your contract is with <strong>' || c.""LegalName"" || '</strong>' ||
                       CASE WHEN COALESCE(c.""CompanyNumber"", '') = '' THEN '' ELSE ', company number ' || c.""CompanyNumber"" END ||
                       ', registered at ' || c.""RegisteredAddress"" || '.</p>' ||
                     '<h2>Prices and payment</h2><p>Prices are shown on the menu and include VAT. The total you see before you pay is the total you pay. We don''t add service or booking fees. Payment is taken when you place your order and is handled by Stripe; we never see or store your card details.</p>' ||
                     '<h2>When we start cooking</h2><p>We begin preparing your order as soon as your payment is confirmed. Your order isn''t accepted, and nothing is charged, until that happens.</p>' ||
                     '<h2>Allergies and intolerances</h2><p>Each dish lists the allergens it contains and any it may contain through cross-contamination. If you have an allergy or intolerance, please also speak to a member of staff before ordering. They can tell you how a dish is prepared and whether we can make it safely for you. Please don''t rely on the notes box alone.</p>' ||
                     '<h2>Collection</h2><p>Orders are for collection at the counter. We''ll hold your order for ' || c.""CollectionHoldMinutes""::text || ' minutes after it''s ready, after which we may dispose of it for food safety reasons and won''t be able to refund it.</p>' ||
                     '<h2>Changing your mind</h2><p>Because food is prepared fresh to order, the 14-day right to cancel under the Consumer Contracts Regulations 2013 does not apply. If you need to change or cancel, speak to staff straight away. If we haven''t started your order we''ll usually be able to help.</p>'),
                    (1,
                     '<h1>Refunds</h1>' ||
                     '<p><strong>We''ll refund you in full if:</strong> we cancel your order, we can''t make something you''ve paid for, or your order isn''t of satisfactory quality and you tell staff before you leave.</p>' ||
                     '<p><strong>We may not refund if:</strong> you change your mind after we''ve started preparing your order, or you don''t collect it within ' || c.""CollectionHoldMinutes""::text || ' minutes of it being ready.</p>' ||
                     '<p><strong>How refunds work.</strong> Refunds are made in full to the card you paid with. We can''t refund to a different card or in cash. Your bank usually takes 5-10 working days to show it.</p>' ||
                     '<p><strong>Part of an order.</strong> If only part of your order was wrong, speak to a member of staff at the venue.</p>' ||
                     '<h2>Contact</h2><p>' || c.""ContactEmail"" || COALESCE(' ' || c.""ContactPhone"", '') || '</p>'),
                    (2,
                     '<h1>Privacy policy</h1>' ||
                     '<p>This site is operated by <strong>' || c.""LegalName"" || '</strong>' ||
                       CASE WHEN COALESCE(c.""CompanyNumber"", '') = '' THEN '' ELSE ', company number ' || c.""CompanyNumber"" END ||
                       ', registered at ' || c.""RegisteredAddress"" || '. We are the data controller for the information described below. The site itself is built and hosted for us by Lanyard, which processes that information only on our instructions.</p>' ||
                     '<h2>What we collect when you order food</h2><p>Ordering is anonymous. We don''t ask for your name, and you don''t create an account. When you place an order we record the table you scanned, what you ordered and the price, the allergens declared for those dishes, and anything you type in the notes box. We also keep a reference to the payment so it can be matched up or refunded.</p>' ||
                     '<p>The notes box is free text and people often use it to tell us about an allergy or intolerance. That is information about your health, so we keep it only as long as it is useful: <strong>we automatically delete the contents of the notes box 30 days after the order</strong>. The rest of the order is kept as a sales and food-safety record.</p>' ||
                     '<h2>Card payments</h2><p>Card payments are handled by Stripe. Your card details are entered directly into Stripe''s own payment form and never reach this site or our systems; we only ever see a reference to the payment, the amount and whether it succeeded.</p>' ||
                     '<h2>How long we keep it</h2><ul><li>Notes box: deleted automatically 30 days after the order.</li><li>The order itself (items, prices, allergens, payment reference): kept for six years.</li></ul>' ||
                     '<h2>Cookies</h2><p>We use only strictly-necessary cookies and equivalent browser storage: enough to keep the site working and secure, and to remember your basket while you''re ordering. There is no advertising or analytics tracking on this site.</p>' ||
                     '<h2>Your rights</h2><p>You have the right to ask for a copy of the information we hold about you, to have it corrected or deleted, and to object to or restrict how we use it. If you''re unhappy with how we''ve handled your information you can complain to the Information Commissioner''s Office at ico.org.uk.</p>' ||
                     '<h2>Contact</h2><p>' || c.""ContactEmail"" || COALESCE(' ' || c.""ContactPhone"", '') || '</p>')
                ) AS d(doc_type, body)
                WHERE COALESCE(c.""LegalName"", '') <> ''
                  AND COALESCE(c.""RegisteredAddress"", '') <> ''
                  AND COALESCE(c.""ContactEmail"", '') <> ''
                  AND NOT EXISTS (
                      SELECT 1 FROM ""CompanyLegalDocuments"" existing
                      WHERE existing.""CompanyId"" = c.""Id"" AND existing.""DocumentType"" = d.doc_type);
            ");

            // Documents saved before this change were written against {{Token}} placeholders that
            // were substituted at render time. Nothing substitutes them any more, so they would
            // appear literally on a customer's checkout as "{{ContactEmail}}". Filled in here,
            // from the same columns, while those columns still exist.
            migrationBuilder.Sql(@"
                UPDATE ""CompanyLegalDocuments"" d
                SET ""BodyHtml"" =
                    replace(replace(replace(replace(replace(replace(replace(
                        d.""BodyHtml"",
                        '{{LegalName}}',             COALESCE(c.""LegalName"", c.""Name"")),
                        '{{CompanyName}}',           c.""Name""),
                        '{{CompanyNumber}}',         COALESCE(c.""CompanyNumber"", '')),
                        '{{RegisteredAddress}}',     COALESCE(c.""RegisteredAddress"", '')),
                        '{{ContactEmail}}',          COALESCE(c.""ContactEmail"", '')),
                        '{{ContactPhone}}',          COALESCE(c.""ContactPhone"", '')),
                        '{{CollectionHoldMinutes}}', c.""CollectionHoldMinutes""::text)
                FROM ""Companies"" c
                WHERE c.""Id"" = d.""CompanyId"" AND d.""BodyHtml"" LIKE '%{{%';
            ");

            migrationBuilder.DropColumn(name: "CollectionHoldMinutes", table: "Companies");
            migrationBuilder.DropColumn(name: "CompanyNumber", table: "Companies");
            migrationBuilder.DropColumn(name: "ContactEmail", table: "Companies");
            migrationBuilder.DropColumn(name: "ContactPhone", table: "Companies");
            migrationBuilder.DropColumn(name: "LegalName", table: "Companies");
            migrationBuilder.DropColumn(name: "RegisteredAddress", table: "Companies");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsPublished",
                table: "CompanyLegalDocuments");

            migrationBuilder.AddColumn<int>(
                name: "CollectionHoldMinutes",
                table: "Companies",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "CompanyNumber",
                table: "Companies",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContactEmail",
                table: "Companies",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContactPhone",
                table: "Companies",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LegalName",
                table: "Companies",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RegisteredAddress",
                table: "Companies",
                type: "text",
                nullable: true);
        }
    }
}
