import { useCallback, useId, useMemo } from "react";
import { Form } from "react-bootstrap";
import styles from "./multi-checkbox-input.module.css";

type MultiCheckboxInputProps = {
    options: Array<string | { value: string; label: string }>;
    value: string;
    onChange: (value: string) => void;
};

export function MultiCheckboxInput({ options, value, onChange }: MultiCheckboxInputProps) {
    const idPrefix = useId().replaceAll(":", "");
    const normalizedOptions = useMemo(() => options.map(option =>
        typeof option === "string" ? { value: option, label: option } : option), [options]);
    const selectedOptions = useMemo(() => {
        if (!value || value.trim() === "") return [];
        return value.split(",").map(c => c.trim()).filter(c => c.length > 0);
    }, [value]);

    const renderedOptions = useMemo(() => {
        const seen = new Set<string>();
        const selectedOnly = selectedOptions.map(selected => ({ value: selected, label: selected }));
        return [...normalizedOptions, ...selectedOnly].filter(option => {
            if (seen.has(option.value)) return false;
            seen.add(option.value);
            return true;
        });
    }, [normalizedOptions, selectedOptions]);

    const onOptionCheckboxChange = useCallback((option: string, checked: boolean) => {
        let newSelected: string[];
        if (checked) {
            newSelected = selectedOptions.includes(option)
                ? selectedOptions
                : [...selectedOptions, option];
        } else {
            newSelected = selectedOptions.filter(o => o !== option);
        }
        onChange(newSelected.join(", "));
    }, [onChange, selectedOptions]);

    if (renderedOptions.length === 0) {
        return null;
    }

    return (
        <div className={styles.container}>
            {renderedOptions.map((option, index) => {
                const isUnavailable = !normalizedOptions.some(x => x.value === option.value);
                return (
                <Form.Check
                    key={option.value}
                    type="checkbox"
                    id={`${idPrefix}-multi-checkbox-${index}`}
                    label={isUnavailable ? `${option.label} (unavailable)` : option.label}
                    checked={selectedOptions.includes(option.value)}
                    onChange={e => onOptionCheckboxChange(option.value, e.target.checked)}
                />
                );
            })}
        </div>
    );
}
