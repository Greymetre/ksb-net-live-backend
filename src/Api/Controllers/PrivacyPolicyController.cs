using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

/// <summary>The public privacy policy pages.
///
/// Google Play and the App Store both require a policy that opens for anyone,
/// with no login and no app installed, at a URL that does not move. Serving it
/// from the API means it ships and stays alive with the backend rather than
/// depending on somebody remembering to keep a separate page online.
///
/// This returns HTML, not JSON, because a store reviewer opens it in a browser.</summary>
[ApiController]
[AllowAnonymous]
public sealed class PrivacyPolicyController : ControllerBase
{
    [HttpGet("/privacy-policy")]
    [HttpGet("/privacy-policy/vriddhi-ksb")]
    public ContentResult VriddhiKsb() => Html(VriddhiKsbPolicy);

    /// <summary>Google Play asks for a page where an account holder can start a deletion
    /// request without installing the app, and wants it to say plainly what goes and what
    /// is kept. This is that page.</summary>
    [HttpGet("/account-deletion")]
    [HttpGet("/account-deletion/vriddhi-ksb")]
    public ContentResult VriddhiKsbAccountDeletion() => Html(VriddhiKsbDeletion);

    private ContentResult Html(string body)
    {
        Response.Headers["Cache-Control"] = "public, max-age=3600";
        return Content(body, "text/html; charset=utf-8");
    }

    private const string VriddhiKsbPolicy = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Privacy Policy - Vriddhi KSB</title>
<style>
  :root { color-scheme: light; }
  * { box-sizing: border-box; }
  body { margin: 0; background: #f2f7fc; color: #11325b;
         font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Arial, sans-serif;
         line-height: 1.65; }
  .wrap { max-width: 780px; margin: 0 auto; padding: 32px 20px 64px; }
  header { background: linear-gradient(135deg, #11325b, #15599a); color: #fff;
           padding: 40px 20px; }
  header .inner { max-width: 780px; margin: 0 auto; }
  header h1 { margin: 0 0 6px; font-size: 26px; }
  header p { margin: 0; opacity: .85; font-size: 14px; }
  h2 { font-size: 18px; margin: 34px 0 10px; }
  p, li { font-size: 15px; color: #33445c; }
  ul { padding-left: 20px; }
  li { margin-bottom: 6px; }
  table { width: 100%; border-collapse: collapse; margin: 14px 0; font-size: 14px; }
  th, td { text-align: left; padding: 10px 12px; border: 1px solid #d7e2ee; vertical-align: top; }
  th { background: #e8f1fa; color: #11325b; }
  .note { background: #fff; border: 1px solid #d7e2ee; border-radius: 12px; padding: 16px 18px; margin: 20px 0; }
  footer { margin-top: 40px; padding-top: 18px; border-top: 1px solid #d7e2ee; font-size: 13px; color: #566477; }
  a { color: #15599a; }
</style>
</head>
<body>
<header><div class="inner">
  <h1>Privacy Policy</h1>
  <p>Vriddhi KSB retailer loyalty app &middot; Last updated 1 September 2026</p>
</div></header>

<div class="wrap">

<p>This policy explains what the Vriddhi KSB mobile application collects, why it is
collected, who it is shared with and how long it is kept. It applies to the app on
Android and iOS and to the loyalty programme it serves.</p>

<p>The programme is operated by <strong>KSB Limited</strong>. Retailers, dealers and
distributors enrolled in the programme use the app to complete their KYC, submit
purchase invoices, track reward points and request redemption.</p>

<h2>What we collect</h2>

<table>
  <tr><th>Information</th><th>Why it is collected</th></tr>
  <tr>
    <td>Name, mobile number, email address, postal address, firm or shop name, customer type</td>
    <td>To create and identify your account, and to reach you about your invoices and rewards.</td>
  </tr>
  <tr>
    <td>GST number, PAN number, Aadhaar number, and a photograph or scan of each of these documents</td>
    <td>KYC verification, which the programme requires before rewards can be paid.</td>
  </tr>
  <tr>
    <td>Bank account holder name, bank name, account type, account number, IFSC code, and bank proof document</td>
    <td>To pay reward redemptions into your account.</td>
  </tr>
  <tr>
    <td>Invoice photographs, invoice number, invoice date and amount, and the dealer the invoice relates to</td>
    <td>To calculate the reward points earned on a purchase and to verify the claim.</td>
  </tr>
  <tr>
    <td>Reward point balance, redemption requests and their status</td>
    <td>To operate your loyalty wallet.</td>
  </tr>
  <tr>
    <td>Device platform, device name and the installed app version</td>
    <td>To keep your session secure and to tell you when the app must be updated.</td>
  </tr>
</table>

<div class="note">
  <strong>What the app does not collect.</strong>
  The app does not access your location, your contacts, your call logs, your SMS
  messages, your calendar or your microphone. It does not track you across other
  apps or websites, and it contains no advertising.
</div>

<h2>Camera and photo permissions</h2>
<p>The app asks for camera and photo library access for one purpose only: so you can
capture or select images of your KYC documents and your purchase invoices. Images are
uploaded only when you choose to submit them. The app does not browse, scan or upload
anything else from your device storage.</p>

<h2>How we use your information</h2>
<ul>
  <li>To register you in the loyalty programme and verify your KYC.</li>
  <li>To validate the invoices you submit and credit the reward points earned.</li>
  <li>To process redemption requests and transfer the reward amount to your bank account.</li>
  <li>To respond to your queries about your account, invoices or rewards.</li>
  <li>To meet legal, tax and audit obligations that apply to the programme.</li>
</ul>
<p>We do not sell your information, and we do not use it for advertising or profiling.</p>

<h2>Who your information is shared with</h2>
<ul>
  <li><strong>KSB Limited</strong> and its authorised programme staff, who verify KYC, approve invoices and approve redemptions.</li>
  <li><strong>Your assigned dealer or distributor</strong>, who can see the invoices raised for you and your KYC completion status, because the programme runs through them.</li>
  <li><strong>Service providers</strong> who host and maintain the programme's systems, bound by confidentiality and permitted to use the data only for that purpose.</li>
  <li><strong>Banks and payment partners</strong>, to the extent needed to transfer a redemption to your account.</li>
  <li><strong>Government or regulatory authorities</strong>, where disclosure is required by law.</li>
</ul>
<p>Your information is not shared with anyone else.</p>

<h2>How your information is protected</h2>
<p>Data is transmitted over encrypted HTTPS connections and stored on access-controlled
servers. Only authorised programme staff can view KYC documents and bank details, and
each account is reachable only by the people whose role in the programme requires it.
Account numbers are masked in the app wherever the full number is not needed.</p>

<h2>How long it is kept</h2>
<p>Account, KYC and invoice records are kept for as long as you remain enrolled in the
programme, and afterwards for the period that tax, accounting and audit rules require
records of reward payments to be retained. Once no longer required, they are deleted or
anonymised.</p>

<h2>Your choices</h2>
<ul>
  <li><strong>Access and correction.</strong> You can view and update your profile and KYC details inside the app at any time. If a detail cannot be edited after verification, write to us and we will correct it.</li>
  <li><strong>Withdrawing consent.</strong> You can withdraw consent for us to hold your KYC and bank details. Doing so ends your participation in the programme, because rewards cannot be verified or paid without them.</li>
  <li><strong>Account deletion.</strong> You can ask us to delete your account and the personal data held with it by writing to the address below. We will confirm within 30 days. Records we are legally required to retain - for example proof of a reward already paid - are kept for the required period and then deleted.</li>
  <li><strong>Device permissions.</strong> Camera and photo access can be revoked at any time from your device settings. The rest of the app continues to work; you will not be able to upload new documents or invoices until access is restored.</li>
</ul>

<h2>Children</h2>
<p>The app is meant for business users - retailers, dealers and distributors - and is not
directed at children. We do not knowingly collect information from anyone under 18.</p>

<h2>Changes to this policy</h2>
<p>If this policy changes, the updated version will appear at this same address with a new
"last updated" date. Significant changes will also be notified in the app.</p>

<h2>Contact us</h2>
<p>For any question about this policy, or to request access, correction or deletion of
your data, contact the programme's grievance officer:</p>

<div class="note">
  <p style="margin:0 0 6px">The Vriddhi KSB loyalty programme is operated by <strong>KSB Limited</strong>.</p>
  <p style="margin:0 0 10px">Privacy and data requests are handled on its behalf by <strong>Greymetre Consultants Private Limited</strong>.</p>
  <p style="margin:0 0 6px">Email: <a href="mailto:info@greymetre.io">info@greymetre.io</a></p>
  <p style="margin:0">Address: 591, Scheme 114 Part I, Dewas Naka, Niranjanpur, Indore, Madhya Pradesh 452010</p>
</div>

<footer>
  Vriddhi KSB &middot; This policy covers the mobile application only. Version of 1 September 2026.
</footer>

</div>
</body>
</html>
""";

    private const string VriddhiKsbDeletion = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Delete your account - Vriddhi KSB</title>
<style>
  :root { color-scheme: light; }
  * { box-sizing: border-box; }
  body { margin: 0; background: #f2f7fc; color: #11325b;
         font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Arial, sans-serif;
         line-height: 1.65; }
  .wrap { max-width: 780px; margin: 0 auto; padding: 32px 20px 64px; }
  header { background: linear-gradient(135deg, #11325b, #15599a); color: #fff; padding: 40px 20px; }
  header .inner { max-width: 780px; margin: 0 auto; }
  header h1 { margin: 0 0 6px; font-size: 26px; }
  header p { margin: 0; opacity: .85; font-size: 14px; }
  h2 { font-size: 18px; margin: 34px 0 10px; }
  p, li { font-size: 15px; color: #33445c; }
  ul, ol { padding-left: 20px; }
  li { margin-bottom: 6px; }
  table { width: 100%; border-collapse: collapse; margin: 14px 0; font-size: 14px; }
  th, td { text-align: left; padding: 10px 12px; border: 1px solid #d7e2ee; vertical-align: top; }
  th { background: #e8f1fa; color: #11325b; }
  .note { background: #fff; border: 1px solid #d7e2ee; border-radius: 12px; padding: 16px 18px; margin: 20px 0; }
  footer { margin-top: 40px; padding-top: 18px; border-top: 1px solid #d7e2ee; font-size: 13px; color: #566477; }
  a { color: #15599a; }
</style>
</head>
<body>
<header><div class="inner">
  <h1>Delete your account</h1>
  <p>Vriddhi KSB retailer loyalty app &middot; KSB Limited &middot; Last updated 1 September 2026</p>
</div></header>

<div class="wrap">

<p>This page explains how to have your <strong>Vriddhi KSB</strong> account, and the personal
data held with it, deleted. It applies to the account you sign in to with your registered
mobile number.</p>

<h2>How to request deletion</h2>
<ol>
  <li>Send an email to <a href="mailto:info@greymetre.io">info@greymetre.io</a> from the email address registered on your account.</li>
  <li>Use the subject line <strong>Account deletion request - Vriddhi KSB</strong>.</li>
  <li>In the message, give your <strong>registered mobile number</strong> and your <strong>firm or shop name</strong>, so we can identify the account.</li>
</ol>
<p>We may contact you on your registered number to confirm the request is genuine. Once
confirmed, the account is closed and deletion begins.</p>

<div class="note">
  <strong>What closing the account means.</strong>
  You will no longer be able to sign in, and any unredeemed reward points are forfeited.
  Please redeem your balance before requesting deletion - points cannot be restored once
  the account is closed.
</div>

<h2>What is deleted</h2>
<ul>
  <li>Your login and profile - name, mobile number, email address, postal address, firm or shop name.</li>
  <li>Your KYC details and every uploaded document image - GST, PAN, Aadhaar and bank proof.</li>
  <li>Your bank account details held for redemption.</li>
  <li>Your uploaded invoice images.</li>
  <li>Your reward point balance and your redemption request history.</li>
</ul>

<h2>What is kept, and for how long</h2>
<p>A small amount of information cannot be deleted immediately, because tax, accounting and
audit rules require records of money already paid to be retained.</p>

<table>
  <tr><th>Kept</th><th>Why</th><th>For how long</th></tr>
  <tr>
    <td>Records of reward redemptions already paid to you, including the amount and the date</td>
    <td>Statutory tax, accounting and audit obligations</td>
    <td>8 years from the end of the financial year of the payment, then deleted</td>
  </tr>
  <tr>
    <td>Invoice records already used to award points</td>
    <td>Programme audit - these are the proof behind a reward that has been paid</td>
    <td>8 years from the end of the financial year, then deleted</td>
  </tr>
</table>

<p>These retained records are locked to the finance and audit function. They are not used to
contact you, and they are not visible to your dealer or in any app.</p>

<h2>How long it takes</h2>
<p>We acknowledge the request within <strong>7 working days</strong> and complete the deletion
within <strong>30 days</strong>, unless we are still waiting for you to confirm your identity.
You will get a confirmation email once it is done.</p>

<h2>Changed your mind</h2>
<p>If you want to use the programme again after deletion, you will need to register afresh.
The old account, its points and its history cannot be brought back.</p>

<h2>Contact</h2>
<div class="note">
  <p style="margin:0 0 6px">The Vriddhi KSB loyalty programme is operated by <strong>KSB Limited</strong>.</p>
  <p style="margin:0 0 10px">Privacy and data requests are handled on its behalf by <strong>Greymetre Consultants Private Limited</strong>.</p>
  <p style="margin:0 0 6px">Email: <a href="mailto:info@greymetre.io">info@greymetre.io</a></p>
  <p style="margin:0">Address: 591, Scheme 114 Part I, Dewas Naka, Niranjanpur, Indore, Madhya Pradesh 452010</p>
</div>

<footer>
  See also our <a href="/privacy-policy">Privacy Policy</a>. Version of 1 September 2026.
</footer>

</div>
</body>
</html>
""";
}
