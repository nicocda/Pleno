import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { BrowserRouter } from "react-router-dom";
import { App } from "./App";
import { AuthProvider } from "./auth/AuthContext";
import { createOidcManager } from "./auth/oidc";
import { ToastProvider } from "./components/Toasts";
import { PerfProvider } from "./perf/PerfProvider";
import { ThemeProvider } from "./theme/ThemeProvider";
import "./styles.css";

const manager = createOidcManager();

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <PerfProvider>
    <ThemeProvider>
      <ToastProvider>
        <AuthProvider manager={manager}>
          <BrowserRouter>
            <App />
          </BrowserRouter>
        </AuthProvider>
      </ToastProvider>
    </ThemeProvider>
    </PerfProvider>
  </StrictMode>,
);
