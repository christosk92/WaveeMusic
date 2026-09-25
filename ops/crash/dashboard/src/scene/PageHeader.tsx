import {
  Body1,
  Breadcrumb,
  BreadcrumbButton,
  BreadcrumbDivider,
  BreadcrumbItem,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  Title2,
  Toolbar,
  ToolbarButton,
  ToolbarDivider,
  ToolbarGroup,
  Overflow,
  OverflowItem,
  ProgressBar,
  makeStyles,
  tokens,
  useIsOverflowItemVisible,
  useOverflowMenu,
} from "@fluentui/react-components";
import { MoreHorizontal20Regular } from "@fluentui/react-icons";
import { Fragment, type MouseEvent, type ReactElement, type ReactNode } from "react";

const useStyles = makeStyles({
  header: {
    position: "sticky",
    top: 0,
    zIndex: 1,
    backgroundColor: tokens.colorNeutralBackground2,
    paddingTop: tokens.spacingVerticalXXL,
    paddingBottom: tokens.spacingVerticalXL,
    paddingLeft: tokens.spacingHorizontalXXL,
    paddingRight: tokens.spacingHorizontalXXL,
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalS,
    borderBottomWidth: tokens.strokeWidthThin,
    borderBottomStyle: "solid",
    borderBottomColor: tokens.colorNeutralStroke2,
  },
  fetchingBar: {
    position: "absolute",
    left: 0,
    right: 0,
    bottom: "-1px",
  },
  titleRow: {
    display: "flex",
    alignItems: "baseline",
    gap: tokens.spacingHorizontalM,
    flexWrap: "wrap",
  },
  subtitle: {
    color: tokens.colorNeutralForeground3,
  },
  toolbarRow: {
    display: "flex",
    alignItems: "center",
    justifyContent: "space-between",
    gap: tokens.spacingHorizontalM,
    minWidth: 0,
  },
  overflowContainer: {
    display: "flex",
    minWidth: 0,
    flex: 1,
  },
  rightGroup: {
    display: "flex",
    alignItems: "center",
    gap: tokens.spacingHorizontalXS,
    flexShrink: 0,
  },
});

export interface Crumb {
  key: string;
  label: string;
  /** Keep a real `href` so middle-click / open-in-new-tab work; `onClick` gets the event so a page
   *  can `preventDefault()` and navigate client-side (the same rule `AppFrame`'s nav items follow). */
  href?: string;
  onClick?: (event: MouseEvent<HTMLElement>) => void;
}

export interface HeaderCommand {
  id: string;
  node: ReactElement;
}

export interface PageHeaderProps {
  crumbs?: Crumb[];
  title: string;
  subtitle?: ReactNode;
  /** Primary command buttons — participate in the toolbar's `Overflow` behaviour at narrow widths. */
  commands?: HeaderCommand[];
  /** Filter controls (Dropdown/SearchBox) shown after a divider; also overflow-aware. */
  filters?: HeaderCommand[];
  /** Always-visible trailing controls (Refresh, etc.) — kept outside the overflow group. */
  rightCommands?: ReactNode;
  /** A background refetch (filter/range change, Refresh, pagination, a mutation invalidation) — NOT
   *  the initial load, which uses a `Skeleton` instead. Pins an indeterminate `ProgressBar` to the
   *  header's bottom edge, Microsoft-style, so it stays visible while the stale content underneath
   *  (dimmed via `PageBody`'s own `isFetching`) is still on screen. */
  isFetching?: boolean;
}

function OverflowMenuItem({ id, node }: { id: string; node: ReactNode }) {
  const isVisible = useIsOverflowItemVisible(id);
  if (isVisible) return null;
  return <MenuItem>{node}</MenuItem>;
}

function CommandOverflowMenu({ items }: { items: HeaderCommand[] }) {
  const { ref, overflowCount, isOverflowing } = useOverflowMenu<HTMLButtonElement>();
  if (!isOverflowing) return null;
  return (
    <Menu>
      <MenuTrigger disableButtonEnhancement>
        <ToolbarButton
          ref={ref}
          icon={<MoreHorizontal20Regular />}
          aria-label={`${overflowCount} more commands`}
        />
      </MenuTrigger>
      <MenuPopover>
        <MenuList>
          {items.map((item) => (
            <OverflowMenuItem key={item.id} id={item.id} node={item.node} />
          ))}
        </MenuList>
      </MenuPopover>
    </Menu>
  );
}

/** Sticky page header (plan §2.4): breadcrumb + title + subtitle + a command `Toolbar` whose groups
 *  collapse into an overflow `Menu` at narrow widths via `Overflow`/`useOverflowMenu`. */
export function PageHeader({
  crumbs,
  title,
  subtitle,
  commands,
  filters,
  rightCommands,
  isFetching,
}: PageHeaderProps) {
  const styles = useStyles();
  const allOverflowItems = [...(commands ?? []), ...(filters ?? [])];
  const hasToolbar = allOverflowItems.length > 0 || !!rightCommands;

  return (
    <div className={styles.header}>
      {crumbs && crumbs.length > 0 && (
        <Breadcrumb size="small" aria-label="Breadcrumb">
          {crumbs.map((crumb, index) => (
            <Fragment key={crumb.key}>
              <BreadcrumbItem>
                <BreadcrumbButton
                  href={crumb.href}
                  onClick={crumb.onClick}
                  current={index === crumbs.length - 1}
                >
                  {crumb.label}
                </BreadcrumbButton>
              </BreadcrumbItem>
              {index < crumbs.length - 1 && <BreadcrumbDivider />}
            </Fragment>
          ))}
        </Breadcrumb>
      )}
      <div className={styles.titleRow}>
        <Title2>{title}</Title2>
        {subtitle && <Body1 className={styles.subtitle}>{subtitle}</Body1>}
      </div>
      {hasToolbar && (
        <div className={styles.toolbarRow}>
          <div className={styles.overflowContainer}>
            <Overflow minimumVisible={1} padding={40}>
              <Toolbar aria-label={`${title} commands`} size="medium">
                {commands && commands.length > 0 && (
                  <ToolbarGroup role="presentation">
                    {commands.map((cmd) => (
                      <OverflowItem key={cmd.id} id={cmd.id}>
                        {cmd.node}
                      </OverflowItem>
                    ))}
                  </ToolbarGroup>
                )}
                {commands && commands.length > 0 && filters && filters.length > 0 && <ToolbarDivider />}
                {filters && filters.length > 0 && (
                  <ToolbarGroup role="presentation">
                    {filters.map((f) => (
                      <OverflowItem key={f.id} id={f.id}>
                        {f.node}
                      </OverflowItem>
                    ))}
                  </ToolbarGroup>
                )}
                <CommandOverflowMenu items={allOverflowItems} />
              </Toolbar>
            </Overflow>
          </div>
          {rightCommands && <div className={styles.rightGroup}>{rightCommands}</div>}
        </div>
      )}
      {isFetching && (
        <ProgressBar
          thickness="medium"
          shape="square"
          className={styles.fetchingBar}
          aria-label={`Refreshing ${title}`}
        />
      )}
    </div>
  );
}
