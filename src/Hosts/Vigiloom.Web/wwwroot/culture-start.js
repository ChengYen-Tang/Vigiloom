// Blazor Web Apps on .NET 10 don't pass the SSR request culture to WebAssembly.
Blazor.start({ webAssembly: { applicationCulture: document.documentElement.lang } });
