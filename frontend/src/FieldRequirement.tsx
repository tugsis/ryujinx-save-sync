export function FieldRequirement({ value }: { value: string }) {
  return (
    <span
      className={
        "field-requirement " +
        (value.includes("필수") ? "required" : "optional")
      }
    >
      {value}
    </span>
  );
}
