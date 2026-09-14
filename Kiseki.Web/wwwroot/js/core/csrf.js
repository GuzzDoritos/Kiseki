/**
 * Retrieves the ASP.NET Core Anti-Forgery Request Verification Token from the page.
 * Checks for the token inside #antiforgeryForm first, then any anti-forgery input on the page.
 * @returns {string} The anti-forgery token, or an empty string if not found.
 */
export function getAntiForgeryToken() {
    const tokenInput = document.querySelector('#antiforgeryForm input[name="__RequestVerificationToken"]')
        || document.querySelector('input[name="__RequestVerificationToken"]');
    return tokenInput ? tokenInput.value : '';
}
