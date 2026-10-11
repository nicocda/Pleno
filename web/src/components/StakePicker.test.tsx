import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi } from "vitest";
import { StakePicker } from "./StakePicker";

describe("StakePicker", () => {
  it("shows only the quick chips that fit the range, in a single row, and picks one", async () => {
    const onChange = vi.fn();
    const { container } = render(<StakePicker stake={10} onChange={onChange} chips={[1, 10, 50, 100, 500]} min={10} max={100} />);

    expect(screen.getAllByRole("radio").map((r) => r.textContent)).toEqual(["10", "50", "100"]);
    expect(container.querySelector(".chips--stake")).toBeInTheDocument(); // la clase que mantiene todo en una sola fila
    await userEvent.click(screen.getByRole("radio", { name: "50" }));
    expect(onChange).toHaveBeenCalledWith(50);
  });

  it("lets the player type another amount and goes back to the default when it is cleared", async () => {
    const onChange = vi.fn();
    render(<StakePicker stake={10} onChange={onChange} chips={[10]} min={1} max={500} defaultStake={10} />);

    const input = screen.getByLabelText("Otro monto (1 a 500)");
    await userEvent.type(input, "75");
    expect(onChange).toHaveBeenLastCalledWith(75);
    await userEvent.clear(input);
    expect(onChange).toHaveBeenLastCalledWith(10);
  });

  it("can hide the custom amount and be disabled", () => {
    render(<StakePicker stake={10} onChange={() => undefined} chips={[10, 50]} min={1} max={500} allowCustom={false} disabled />);

    expect(screen.queryByLabelText(/Otro monto/)).not.toBeInTheDocument();
    expect(screen.getByRole("radio", { name: "10" })).toBeDisabled();
  });
});
