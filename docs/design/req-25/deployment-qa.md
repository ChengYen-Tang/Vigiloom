# Deployment relationship UI review

The management page extends Vigiloom's existing blue and white service-detail and settings patterns. The service-detail concept does not specify a deployment management form, so this is a review of the new workflow against the existing product style rather than a pixel-for-pixel comparison with that concept.

The isolated SQL-backed browser run captured `deployment-desktop.png` at 1486×1058 and `deployment-mobile.png` at 390×844. The data is synthetic. The first mobile check found that the relationship table overflowed the page; the form and table were constrained and the table made horizontally scrollable. A subsequent visual inspection found that scrolling was not obvious, so a narrow-screen hint was added and the mobile screenshot was recaptured.

The in-app browser was also used to inspect the Administrator settings page and follow its return link to the service detail. The real detail page displayed the attested host and linked to its device. The automated browser test checks Reader access in Server and WebAssembly render modes, absence of the management link for Reader, and mobile page width and table scrolling. HTTP tests submit both Administrator forms and verify Reader and API-credential restrictions.

The screenshots do not establish screen-reader compatibility or complete keyboard accessibility. They do not claim that the operator's attestation proves live deployment.
