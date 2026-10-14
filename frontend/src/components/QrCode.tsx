import QRCode from "qrcode";
import { useEffect, useState } from "react";

/**
 * The QR code for an authenticator app, drawn here in the browser from the otpauth address. The secret in that address never goes to any
 * website or service: the library runs locally and the picture is built into the page as an image.
 */
export function QrCode({ value }: { value: string }) {
  const [source, setSource] = useState<string | null>(null);

  useEffect(() => {
    let current = true;
    QRCode.toString(value, { type: "svg", margin: 1, width: 224 }).then(
      (svg) => {
        if (current) {
          setSource(`data:image/svg+xml;base64,${btoa(svg)}`);
        }
      },
      () => {
        if (current) {
          setSource(null);
        }
      },
    );

    return () => {
      current = false;
    };
  }, [value]);

  return source === null ? null : <img src={source} width={224} height={224} alt="QR code to scan with your authenticator app" className="border border-slate-300" />;
}
