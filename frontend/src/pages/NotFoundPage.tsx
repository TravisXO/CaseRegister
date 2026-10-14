import { Link } from "react-router-dom";

export function NotFoundPage() {
  return (
    <div className="page">
      <h1 className="heading">Page not found</h1>
      <p>
        There is nothing at this address.{" "}
        <Link to="/" className="link">
          Go to the start
        </Link>
      </p>
    </div>
  );
}
