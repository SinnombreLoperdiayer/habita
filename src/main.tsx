import { StrictMode } from "react";
import { createRoot } from "react-dom/client";

import App from "./App";
import { PassView } from "./Checkin";
import { readPassFromHash } from "./lib/checkin";
import { AuthGate } from "./Session";
import "./index.css";

const pass = readPassFromHash();

// El pase del copropietario (#pase=...) se abre sin iniciar sesión.
createRoot(document.getElementById("root")!).render(
  <StrictMode>
    {pass ? <PassView data={pass} /> : <AuthGate><App /></AuthGate>}
  </StrictMode>,
);
